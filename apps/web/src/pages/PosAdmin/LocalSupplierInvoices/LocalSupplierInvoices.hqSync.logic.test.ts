import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { shouldSkipDetailAutoReload } from '../../../utils/detailLoadState'
import {
  batchExecuteActions,
  batchUpdateDetailAction,
  batchUpdateDetails,
  batchUpsertDetails,
  getCheckProductsJob,
  getPasteDetailsJob,
  ensureHqProducts,
  hasUpdateHqProductsResultStatistics,
  startCheckProductsJob,
  startPasteDetailsJob,
  startUpdateHqProductsJob,
  syncInvoicesFromHq,
  updateDetailAction,
  updateHqProducts,
  updateToStorePrices,
} from '../../../services/localSupplierInvoiceService'
import {
  filterInvoiceDetails,
  getActionTypeFilter,
  getBarcodeStatusFilter,
  getDetailStatusStats,
  getProductStatusFilter,
  toggleStatusFilter,
} from './InvoiceEdit/statusFilters'
import {
  compareNullableNumbers,
  compareNullableText,
  filterBarcodeStatusColumn,
  filterBooleanColumn,
  filterProductStatusColumn,
  matchesActionTypeColumnFilter,
  matchesNumberColumnFilter,
  matchesTextColumnFilter,
  serializeNumberColumnFilter,
  serializeTextColumnFilter,
} from './InvoiceEdit/tableColumnFilters'
import { defaultPasteFieldOrder, parsePasteText } from './InvoiceEdit/pasteDetails'
import {
  buildBatchExecuteConfirmText,
  constrainSelectedRowKeysToVisibleDetails,
  countSelectedBatchExecuteActions,
} from './InvoiceEdit/batchExecuteConfirm'
import {
  getNextInvoiceTableScrollTop,
  scheduleInvoiceTableScrollRestore,
  shouldRestoreInvoiceTableScroll,
} from './invoiceTableScroll'
import { DetailAction, type LocalSupplierInvoiceItemDto } from '../../../types/localSupplierInvoice'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${message}。Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

async function assertRejects(execute: () => Promise<unknown>, expectedMessage: string, message: string) {
  try {
    await execute()
  } catch (error) {
    const actualMessage = error instanceof Error ? error.message : String(error)
    assertEqual(actualMessage, expectedMessage, message)
    return
  }

  throw new Error(`${message}。Expected promise to reject`)
}

async function assertRequestErrorPayload(
  execute: () => Promise<unknown>,
  expectedMessage: string,
  message: string,
) {
  try {
    await execute()
  } catch (error: any) {
    const actualMessage = error instanceof Error ? error.message : String(error)
    assertEqual(actualMessage, expectedMessage, message)
    assertEqual(error.payload?.data?.invoiceAddedCount, 1, '400 失败时应保留后端 data payload')
    assertEqual(error.payload?.data?.errors?.[0], '测试页失败', '400 失败时应保留错误列表')
    return
  }

  throw new Error(`${message}。Expected promise to reject`)
}

function createFakeAnimationFrames() {
  let nextFrameId = 1
  const callbacks = new Map<number, () => void>()

  return {
    requestFrame(callback: () => void) {
      const frameId = nextFrameId
      nextFrameId += 1
      callbacks.set(frameId, callback)
      return frameId
    },
    cancelFrame(frameId: number) {
      callbacks.delete(frameId)
    },
    flushNext() {
      const entry = callbacks.entries().next().value as [number, () => void] | undefined
      if (!entry) return false
      const [frameId, callback] = entry
      callbacks.delete(frameId)
      callback()
      return true
    },
    pendingCount() {
      return callbacks.size
    },
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

const pageFile = path.resolve(process.cwd(), 'src/pages/PosAdmin/LocalSupplierInvoices/index.tsx')
const editPageFile = path.resolve(process.cwd(), 'src/pages/PosAdmin/LocalSupplierInvoices/InvoiceEdit/index.tsx')
const invoiceHeaderFormFile = path.resolve(process.cwd(), 'src/pages/PosAdmin/LocalSupplierInvoices/InvoiceEdit/invoiceHeaderForm.ts')
const editCellsFile = path.resolve(process.cwd(), 'src/pages/PosAdmin/LocalSupplierInvoices/InvoiceEdit/EditableCells.tsx')
const pricingEditorFile = path.resolve(process.cwd(), 'src/pages/PosAdmin/LocalSupplierInvoices/InvoiceEdit/PricingEditor.tsx')
const pricingEditorChangesFile = path.resolve(process.cwd(), 'src/pages/PosAdmin/LocalSupplierInvoices/InvoiceEdit/pricingEditorChanges.ts')
const invoicePageStyleFile = path.resolve(process.cwd(), 'src/pages/PosAdmin/LocalSupplierInvoices/localSupplierInvoices.css')
const routesFile = path.resolve(process.cwd(), 'src/router/routes.tsx')
const legacyDetailPageFile = path.resolve(process.cwd(), 'src/pages/PosAdmin/LocalSupplierInvoiceDetailPage/index.tsx')
const barcodePreviewFile = path.resolve(process.cwd(), 'src/components/BarcodePreview.tsx')
const serviceFile = path.resolve(process.cwd(), 'src/services/localSupplierInvoiceService.ts')
const typeFile = path.resolve(process.cwd(), 'src/types/localSupplierInvoice.ts')
const globalStyleFile = path.resolve(process.cwd(), 'src/styles/global.css')
const pageSource = readFileSync(pageFile, 'utf8')
const editPageSource = readFileSync(editPageFile, 'utf8')
const invoiceHeaderFormSource = readFileSync(invoiceHeaderFormFile, 'utf8')
const editCellsSource = readFileSync(editCellsFile, 'utf8')
const pricingEditorSource = readFileSync(pricingEditorFile, 'utf8')
const pricingEditorChangesSource = readFileSync(pricingEditorChangesFile, 'utf8')
const invoicePageStyleSource = readFileSync(invoicePageStyleFile, 'utf8')
const routesSource = readFileSync(routesFile, 'utf8')
const barcodePreviewSource = readFileSync(barcodePreviewFile, 'utf8')
const serviceSource = readFileSync(serviceFile, 'utf8')
const typeSource = readFileSync(typeFile, 'utf8')
const globalStyleSource = readFileSync(globalStyleFile, 'utf8')

async function main() {
  const failures: string[] = []

  const typeFailure = await runTest('同步请求和结果类型应声明页面契约字段', () => {
    assert(typeSource.includes('LocalSupplierInvoiceHqSyncRequest'), '应声明同步请求类型')
    assert(typeSource.includes('selectedStoreCodes?: string[]'), '请求应支持 selectedStoreCodes')
    assert(typeSource.includes('startDate?: string'), '请求应支持 startDate')
    assert(typeSource.includes('endDate?: string'), '请求应支持 endDate')
    assert(typeSource.includes('invoiceAddedCount: number'), '结果应支持主表新增计数')
    assert(typeSource.includes('detailUpdatedCount: number'), '结果应支持明细更新计数')
  })
  if (typeFailure) failures.push(typeFailure)

  const ensureHqTypeFailure = await runTest('编辑页商品同步到HQ应声明专用契约字段', () => {
    assert(typeSource.includes('EnsureHqProductsRequest'), '应声明 EnsureHqProductsRequest')
    assert(typeSource.includes('detailGuids: string[]'), '商品同步请求应强制传 detailGuids')
    assert(typeSource.includes('targetStoreCodes: string[]'), '商品同步请求应强制传 targetStoreCodes，避免后端扩大写入范围')
    assert(typeSource.includes('idempotencyKey?: string'), '商品同步请求应支持 idempotencyKey')
    assert(typeSource.includes('EnsureHqProductsResult'), '应声明 EnsureHqProductsResult')
    assert(typeSource.includes('hqPurchasePricesUpdated: number'), '商品同步结果应包含 HQ 分店进货价更新计数')
    assert(typeSource.includes('errors: EnsureHqProductError[]'), '商品同步结果应包含逐行错误列表')
    assert(!typeSource.includes('updateHqProduct?: boolean'), '更新到分店请求不应再携带同步 HQ 开关')
    assert(typeSource.includes('UpdateHqProductsRequest'), '应声明字段级更新 HQ 请求类型')
    assert(typeSource.includes('updateFields: UpdateToStorePricesFields'), '字段级更新 HQ 请求应复用字段选择契约')
    assert(typeSource.includes('UpdateHqProductsResult'), '应声明字段级更新 HQ 结果类型')
    assert(typeSource.includes('hqRetailPricesUpdated?: number'), '字段级更新 HQ 结果应包含零售价更新计数')
    assert(typeSource.includes('hqDiscountRatesUpdated?: number'), '字段级更新 HQ 结果应包含折扣率更新计数')
    assert(typeSource.includes('hqProductSetCodesCreated?: number'), '字段级更新 HQ 结果应包含 HQ 一品多码新增计数')
    assert(typeSource.includes('hqProductSetCodesUpdated?: number'), '字段级更新 HQ 结果应包含 HQ 一品多码更新计数')
    assert(typeSource.includes('hqStoreMultiCodesCreated?: number'), '字段级更新 HQ 结果应包含 HQ 分店一品多码新增计数')
    assert(typeSource.includes('hqStoreMultiCodesUpdated?: number'), '字段级更新 HQ 结果应包含 HQ 分店一品多码更新计数')
    assert(typeSource.includes('LocalSupplierInvoiceBatchJobStatus'), '本地进货单批量后台任务应声明状态类型')
    assert(typeSource.includes('UpdateToStorePricesJobResult'), '更新到分店应声明后台任务结果类型')
    assert(typeSource.includes('UpdateHqProductsJobResult'), '更新HQ商品应声明后台任务结果类型')
    assert(typeSource.includes('isDuplicateRequest?: boolean'), '后台任务结果应支持重复提交标记')
    assert(typeSource.includes('PasteDetailsJobResult'), '粘贴明细应声明后台任务结果类型')
    assert(typeSource.includes('result?: BatchResultDto'), '粘贴明细后台任务 result 应复用 BatchResultDto')
    assert(typeSource.includes('CheckProductsJobResult'), '商品检测应声明后台任务结果类型')
    assert(typeSource.includes('result?: CheckProductsResponse'), '商品检测后台任务 result 应复用 CheckProductsResponse')
  })
  if (ensureHqTypeFailure) failures.push(ensureHqTypeFailure)

  const updateHqMultiCodeResultFailure = await runTest('编辑页更新HQ商品结果应展示多码同步统计', () => {
    assert(editPageSource.includes('result.hqProductSetCodesCreated'), '结果弹窗应展示 HQ 一品多码新增数量')
    assert(editPageSource.includes('result.hqProductSetCodesUpdated'), '结果弹窗应展示 HQ 一品多码更新数量')
    assert(editPageSource.includes('result.hqStoreMultiCodesCreated'), '结果弹窗应展示 HQ 分店一品多码新增数量')
    assert(editPageSource.includes('result.hqStoreMultiCodesUpdated'), '结果弹窗应展示 HQ 分店一品多码更新数量')
  })
  if (updateHqMultiCodeResultFailure) failures.push(updateHqMultiCodeResultFailure)

  const invoiceDetailKeepAliveFailure = await runTest('分店进货单详情 Tab 切回已有进货单时应跳过自动刷新', () => {
    for (const [pageName, source] of [
      ['明细页', editPageSource],
    ] as const) {
      assert(
        source.includes('loadedInvoiceGuidRef') &&
          source.includes('useKeepAliveContext') &&
          source.includes('const { active } = useKeepAliveContext()') &&
          source.includes('if (!active) return') &&
          source.includes('visibleInvoiceGuidRef') &&
          source.includes('lastLoadedManagedStoreCodeKeyRef') &&
          source.includes('shouldSkipDetailAutoReload({') &&
          source.includes('requestedDetailQueryKey: managedStoreCodeKey') &&
          source.includes('loadedDetailQueryKey: lastLoadedManagedStoreCodeKeyRef.current') &&
          source.includes('shouldShowDetailInitialLoading({') &&
          source.includes('active,') &&
          source.includes('return'),
        `${pageName} 缺少 KeepAlive active 守卫，隐藏 Tab 会跟随全局路由变化重新请求`,
      )
      assert(
        source.includes('const loadDetails') &&
          source.includes('showLoading = true') &&
          source.includes('if (showLoading)') &&
          source.includes('setDetailLoading(true)') &&
          source.includes('setDetailLoading(false)') &&
          source.includes('await loadDetails(showLoading)'),
        `${pageName} 明细加载应保留 showLoading 参数；手动或业务显式刷新仍应可显示 loading`,
      )
      assert(
        source.includes('invoiceSnapshotRef') &&
          source.includes('detailsSnapshotRef') &&
          source.includes('areLocalSupplierInvoicesEqual(invoiceSnapshotRef.current, data)') &&
          source.includes('areLocalSupplierInvoiceDetailsEqual(detailsSnapshotRef.current, data)'),
        `${pageName} 后台返回相同订单头和明细时应跳过 setFieldsValue/setDetails，避免相同数据重绘一闪`,
      )
    }
    assert(
      editPageSource.includes('additionalBarcodes: item.additionalBarcodes'),
      '编辑页明细快照应包含 additionalBarcodes，副码变化后粘贴完成 reload 才会刷新表格提示',
    )
    assert(
      shouldSkipDetailAutoReload({
        requestedDetailId: 'invoice-1',
        loadedDetailId: 'invoice-1',
        visibleDetailId: 'invoice-1',
        requestedDetailQueryKey: '1012',
        loadedDetailQueryKey: '1012',
      }) &&
        !shouldSkipDetailAutoReload({
          requestedDetailId: 'invoice-2',
          loadedDetailId: 'invoice-1',
          visibleDetailId: 'invoice-1',
        }) &&
        !shouldSkipDetailAutoReload({
          requestedDetailId: 'invoice-1',
          loadedDetailId: 'invoice-1',
          visibleDetailId: 'invoice-1',
          requestedDetailQueryKey: '1012',
          loadedDetailQueryKey: '1033',
        }),
      '同进货单且权限范围一致时应跳过自动刷新，切换新进货单或权限范围变化时不应跳过',
    )
  })
  if (invoiceDetailKeepAliveFailure) failures.push(invoiceDetailKeepAliveFailure)

  // 原「查看」页（店长用）已并入明细页：同一页面按权限收起处理操作，旧地址重定向保留书签。
  const mergedDetailPageFailure = await runTest('查看页应并入明细页并按权限显示只读视图', () => {
    assert(!existsSync(legacyDetailPageFile), '旧查看页文件应已删除，避免两套明细页继续分叉')
    assert(!routesSource.includes('LocalSupplierInvoiceDetailPage'), '路由不应再加载旧查看页')
    assert(routesSource.includes('function LegacyInvoiceDetailRedirect()'), '旧 invoice-detail 地址应保留重定向组件')
    assert(
      routesSource.includes("path: '/pos-admin/invoice-detail/:id'") && routesSource.includes('element: <LegacyInvoiceDetailRedirect />'),
      '旧查看地址应重定向到同一张单的新明细页',
    )
    const editRouteStart = routesSource.indexOf("path: '/pos-admin/local-supplier-invoices/:id',")
    const editRouteSource = routesSource.slice(editRouteStart, routesSource.indexOf('element: <InvoiceEditPage />', editRouteStart))
    assert(editRouteSource.includes("accessKey: 'canManageLocalPurchase'"), '明细页应对只有查看权限的用户开放')
    assert(editPageSource.includes('const canEditInvoice = access.canEditLocalPurchase'), '表头编辑应要求进货单编辑权限（后端 PUT 同口径）')
    assert(editPageSource.includes('const canEditDetailRows = isAdmin'), '明细行编辑沿用原页面的管理员口径')
    assert(editPageSource.includes('readOnly={!canEditDetailRows}'), '无明细编辑权限时行内单元格应只读')
    assert(editPageSource.includes("rowSelection={canSelectRows ? {"), '没有任何批量操作权限时不应显示勾选列')
    assert(editPageSource.includes("key: 'hqStatus'"), '只读视图应用总部处理状态列代替商品匹配与操作列')
  })
  if (mergedDetailPageFailure) failures.push(mergedDetailPageFailure)

  const invoiceListScrollStateFailure = await runTest('分店进货单列表只在活动 Tab 记录滚动并仅在重新激活时恢复', () => {
    assertEqual(getNextInvoiceTableScrollTop(true, 120, 360), 360, '活动 Tab 应记录最新表体滚动位置')
    assertEqual(getNextInvoiceTableScrollTop(false, 360, 0), 360, '隐藏 Tab 的归零滚动事件不应覆盖已保存位置')
    assert(shouldRestoreInvoiceTableScroll(false, true), '隐藏 Tab 重新激活时应恢复滚动位置')
    assert(!shouldRestoreInvoiceTableScroll(true, true), '持续活动时不应重复恢复滚动位置')
    assert(!shouldRestoreInvoiceTableScroll(true, false), '切到隐藏状态时不应恢复滚动位置')
    assert(!shouldRestoreInvoiceTableScroll(false, false), '持续隐藏时不应恢复滚动位置')
  })
  if (invoiceListScrollStateFailure) failures.push(invoiceListScrollStateFailure)

  const invoiceListScrollScheduleFailure = await runTest('分店进货单列表应等待两帧且只恢复一次滚动位置', () => {
    const frames = createFakeAnimationFrames()
    let restoreCount = 0
    const cleanup = scheduleInvoiceTableScrollRestore({
      requestFrame: frames.requestFrame,
      cancelFrame: frames.cancelFrame,
      restore: () => {
        restoreCount += 1
      },
    })

    assertEqual(frames.pendingCount(), 1, '调度后应只等待第一帧')
    assert(frames.flushNext(), '第一帧应存在')
    assertEqual(restoreCount, 0, '第一帧只等待布局，不应立即恢复')
    assertEqual(frames.pendingCount(), 1, '第一帧结束后应等待第二帧')
    assert(frames.flushNext(), '第二帧应存在')
    assertEqual(restoreCount, 1, '第二帧应且只应恢复一次')
    assertEqual(frames.pendingCount(), 0, '恢复后不应遗留待执行帧')
    cleanup()
  })
  if (invoiceListScrollScheduleFailure) failures.push(invoiceListScrollScheduleFailure)

  const invoiceListScrollCancelFailure = await runTest('分店进货单列表快速切换时应取消旧的滚动恢复', () => {
    const beforeFirstFrame = createFakeAnimationFrames()
    let cancelledBeforeFirstFrameCount = 0
    const cancelBeforeFirstFrame = scheduleInvoiceTableScrollRestore({
      requestFrame: beforeFirstFrame.requestFrame,
      cancelFrame: beforeFirstFrame.cancelFrame,
      restore: () => {
        cancelledBeforeFirstFrameCount += 1
      },
    })
    cancelBeforeFirstFrame()
    assertEqual(beforeFirstFrame.pendingCount(), 0, '第一帧前清理应取消当前待执行帧')
    assertEqual(cancelledBeforeFirstFrameCount, 0, '第一帧前清理不应恢复滚动位置')

    const rapidSwitchFrames = createFakeAnimationFrames()
    let rapidSwitchRestoreCount = 0
    const cancelOldRestore = scheduleInvoiceTableScrollRestore({
      requestFrame: rapidSwitchFrames.requestFrame,
      cancelFrame: rapidSwitchFrames.cancelFrame,
      restore: () => {
        rapidSwitchRestoreCount += 1
      },
    })
    assert(rapidSwitchFrames.flushNext(), '旧恢复的第一帧应存在')
    cancelOldRestore()
    assertEqual(rapidSwitchFrames.pendingCount(), 0, '第一帧后清理应取消旧恢复的第二帧')

    const cleanupLatestRestore = scheduleInvoiceTableScrollRestore({
      requestFrame: rapidSwitchFrames.requestFrame,
      cancelFrame: rapidSwitchFrames.cancelFrame,
      restore: () => {
        rapidSwitchRestoreCount += 1
      },
    })
    assert(rapidSwitchFrames.flushNext(), '最新恢复的第一帧应存在')
    assert(rapidSwitchFrames.flushNext(), '最新恢复的第二帧应存在')
    assertEqual(rapidSwitchRestoreCount, 1, '快速切换后只能执行最新一次恢复')
    cleanupLatestRestore()
  })
  if (invoiceListScrollCancelFailure) failures.push(invoiceListScrollCancelFailure)

  const invoiceListScrollRestoreFailure = await runTest('分店进货单列表页应接入可测试的滚动恢复策略', () => {
    assert(
      pageSource.includes("import { useKeepAliveContext } from 'keepalive-for-react'") &&
        pageSource.includes('const { active } = useKeepAliveContext()') &&
        pageSource.includes('const invoiceTableRef = useRef<TableRef | null>(null)') &&
        pageSource.includes('ref={invoiceTableRef}') &&
        pageSource.includes('onScroll={handleInvoiceTableScroll}'),
      '列表页应接入 KeepAlive active 状态、AntD TableRef 和表体滚动事件',
    )
    assert(
      pageSource.includes("from './invoiceTableScroll'") &&
        pageSource.includes('getNextInvoiceTableScrollTop(') &&
        pageSource.includes('shouldRestoreInvoiceTableScroll(wasActive, active)') &&
        pageSource.includes('return scheduleInvoiceTableScrollRestore({'),
      '列表页应调用已通过行为测试的滚动保存、激活判断和双帧恢复策略',
    )
  })
  if (invoiceListScrollRestoreFailure) failures.push(invoiceListScrollRestoreFailure)

  const jobTypeFailure = await runTest('更新到分店和更新HQ商品应声明后台 Job 契约字段', () => {
    assert(typeSource.includes('export interface LocalSupplierInvoiceJobBase'), '应声明本地进货单后台 Job 基础类型')
    assert(typeSource.includes('jobId: string'), '后台 Job 应声明 jobId')
    assert(typeSource.includes('targetStoreCodes?: string[]'), '后台 Job 应声明目标分店编码用于权限复验')
    assert(typeSource.includes('operationId: string'), '后台 Job 应声明 operationId')
    assert(typeSource.includes('status:'), '后台 Job 应声明 status')
    assert(typeSource.includes('isDuplicateRequest?: boolean'), '后台 Job 应声明 isDuplicateRequest')
    assert(typeSource.includes('createdAt?: string'), '后台 Job 应声明 createdAt')
    assert(typeSource.includes('completedAt?: string'), '后台 Job 应声明 completedAt')
    assert(typeSource.includes('expiresAt?: string'), '后台 Job 应声明 expiresAt')
    assert(typeSource.includes('message?: string'), '后台 Job 应声明 message')
    assert(typeSource.includes('export interface UpdateToStorePricesJobDto'), '应声明更新到分店 Job DTO')
    assert(typeSource.includes('result?: UpdateToStorePricesResult'), '更新到分店 Job result 应复用 UpdateToStorePricesResult')
    assert(typeSource.includes('export interface UpdateHqProductsJobDto'), '应声明更新HQ商品 Job DTO')
    assert(typeSource.includes('result?: UpdateHqProductsResult'), '更新HQ商品 Job result 应复用 UpdateHqProductsResult')
  })
  if (jobTypeFailure) failures.push(jobTypeFailure)

  // HQ 进货单 → HBweb 的「从HQ同步」已于 2026-09-29 停用（后端返回 410），列表页不得再提供入口。
  const pageButtonFailure = await runTest('列表页不再提供从HQ同步按钮与弹窗', () => {
    for (const removed of [
      'syncInvoicesFromHq',
      'CloudSyncOutlined',
      "t('posAdmin.invoices.syncFromHQ'",
      'setHqSyncModalOpen',
      'hqSyncForm',
      'handleSyncFromHq',
      'getHqSyncResultFromError',
    ]) {
      assert(!pageSource.includes(removed), `列表页不应再包含 HQ 同步入口代码：${removed}`)
    }
    assert(
      pageSource.includes("t('posAdmin.invoices.import.uploadButton')") &&
        pageSource.includes('onClick={() => setImportVisible(true)}'),
      '列表页应保留管理员的导入入口',
    )
  })
  if (pageButtonFailure) failures.push(pageButtonFailure)

  const listPaginationLayoutFailure = await runTest('列表页表格滚动区域不应覆盖外置分页', () => {
    assert(pageSource.includes('const tableRegionRef = useRef<HTMLDivElement>(null)'), '列表页应声明表格区域 ref')
    assert(pageSource.includes("region.querySelector('.ant-table-thead')"), '表体高度计算必须扣除 AntD 表头')
    assert(pageSource.includes('horizontalScrollbarHeight'), '表体高度计算必须扣除横向滚动条高度')
    assert(
      pageSource.includes('region.clientHeight - tableHeaderHeight - horizontalScrollbarHeight - 8'),
      '表体高度必须按表格区域扣除表头和横向滚动条计算',
    )
    assert(
      pageSource.includes('window.requestAnimationFrame') && pageSource.includes('ResizeObserver'),
      '表格高度应在布局变化后重新测量',
    )
    assert(
      pageSource.includes('ref={tableRegionRef}') &&
        pageSource.includes('flex: 1') &&
        pageSource.includes('minHeight: 0') &&
        pageSource.includes("overflow: 'hidden'"),
      '表格区域必须裁剪溢出，避免固定列画到分页栏',
    )
    assert(
      pageSource.includes("position: 'relative'") &&
        pageSource.includes('zIndex: 3') &&
        pageSource.includes('flexShrink: 0'),
      '分页栏应保持独立层级和固定底部空间',
    )
  })
  if (listPaginationLayoutFailure) failures.push(listPaginationLayoutFailure)

  const editPageButtonFailure = await runTest('编辑页应使用专用权限显示更新HQ商品按钮', () => {
    assert(editPageSource.includes('canWriteLocalPurchaseToHq'), '编辑页应使用可编辑本地进货 + PushToHq 的组合权限控制写 HQ 入口')
    assert(editPageSource.includes("t('posAdmin.invoiceDetail.updateHqProductsBtn', '更新HQ商品')"), '编辑页应显示更新HQ商品按钮文案')
    assert(editPageSource.includes('setHqUpdateVisible(true)'), '更新HQ商品按钮应打开独立弹窗')
    assert(editPageSource.includes('disabled={hqUpdateLoading || !selectedRowKeys.length}'), '更新HQ商品按钮必须要求已选择明细')
    assert(editPageSource.includes('handleUpdateHqProducts'), '编辑页应实现字段级更新HQ处理函数')
    assert(editPageSource.includes('startUpdateHqProductsJob(invoiceGuid'), '编辑页应提交字段级更新 HQ 后台任务')
    assert(editPageSource.includes('getUpdateHqProductsJob(invoiceGuid'), '编辑页应轮询字段级更新 HQ 后台任务')
    assert(editPageSource.includes("completedJob.status === 'Failed'"), '更新HQ商品轮询到 Failed Job 时应展示失败而不是完成')
    assert(!editPageSource.includes('const result = await updateHqProducts(invoiceGuid'), '编辑页不应再直接等待字段级更新 HQ 长请求')
    assert(editPageSource.includes('if (hqUpdateLoading) return'), '更新HQ商品应阻止重复请求')
    assert(editPageSource.includes('hqUpdateIdempotencyKeyRef.current'), '更新HQ商品应在请求周期内复用稳定 idempotencyKey')
  })
  if (editPageButtonFailure) failures.push(editPageButtonFailure)

  const updateHqConflictDisplayFailure = await runTest('更新HQ商品重复任务冲突不应显示全零失败统计', () => {
    assertEqual(
      hasUpdateHqProductsResultStatistics({ existingJobId: 'hq-job-running' }),
      false,
      '仅包含 existingJobId 的冲突 payload 不是 HQ 更新统计',
    )
    assertEqual(
      hasUpdateHqProductsResultStatistics({ errors: ['普通校验错误'] }),
      false,
      '仅包含错误列表的普通失败 payload 不是 HQ 更新统计',
    )
    assertEqual(
      hasUpdateHqProductsResultStatistics({ failed: '0' }),
      false,
      '统计计数字段类型错误时不能伪造成 HQ 更新统计',
    )
    assertEqual(
      hasUpdateHqProductsResultStatistics({ total: 0, updated: 0, failed: 0, errors: [] }),
      true,
      '后端真实返回全零统计时仍应识别为 HQ 更新统计',
    )
    assert(
      editPageSource.includes('if (!hasUpdateHqProductsResultStatistics(candidate)) return undefined'),
      '409 仅返回 existingJobId 时应保留后端冲突消息，不能伪造成失败 0 条',
    )
  })
  if (updateHqConflictDisplayFailure) failures.push(updateHqConflictDisplayFailure)

  const editPageImportFailure = await runTest('编辑页不应动态导入已静态使用的本地供应商进货单服务', () => {
    assert(
      !editPageSource.includes("await import('../../../../services/localSupplierInvoiceService')"),
      '编辑页已静态导入该服务，不能再动态导入同一模块，否则 Vite 会提示动态导入无法拆分 chunk',
    )
    assert(editPageSource.includes('updateDetailAction,'), '编辑页应静态导入更新行操作接口')
  })
  if (editPageImportFailure) failures.push(editPageImportFailure)

  const editPageDynamicTabTitleFailure = await runTest('编辑页 Tab 标题应在发票加载后显示业务标题', () => {
    assert(
      editPageSource.includes("import { useDynamicTabTitle } from '../../../../hooks/useDynamicTabTitle'"),
      '编辑页必须 import useDynamicTabTitle',
    )
    assert(editPageSource.includes('useDynamicTabTitle(invoiceTabTitle)'), '编辑页必须在组件顶层调用 useDynamicTabTitle')
    assert(editPageSource.includes('function buildInvoiceTabTitle('), '编辑页应提供本页专用标题格式函数')
    assert(editPageSource.includes('invoice.storeName?.trim()'), '标题函数必须优先读取 storeName')
    assert(editPageSource.includes('invoice.storeCode?.trim()'), '分店名称缺失时必须回退 storeCode')
    assert(editPageSource.includes('invoice.supplierName?.trim().slice(0, 4).toUpperCase()'), '供应商名称必须 trim 后取前 4 位并转大写')
    assert(editPageSource.includes('invoice.supplierCode?.trim().slice(0, 4).toUpperCase()'), '供应商名称缺失时必须回退 supplierCode 前 4 位')
    assert(editPageSource.includes('invoice.invoiceNo?.trim()'), '标题函数必须读取 invoiceNo')
    assert(editPageSource.includes('[storeSegment, supplierSegment, invoiceNoSegment].filter(Boolean).join'), '标题函数应过滤缺失字段后用空格拼接')
    assert(
      editPageSource.includes("t('menu.invoiceDetail', '进货单详情')"),
      '未加载发票前必须保留进货单详情 fallback，避免 Tab 空白（只读用户也会打开此页）',
    )
    assert(
      editPageSource.includes('这里只更新当前编辑页的 KeepAlive Tab 标题，不改变路由标题或面包屑。'),
      '应用中文注释说明动态标题只影响当前编辑页 Tab',
    )
  })
  if (editPageDynamicTabTitleFailure) failures.push(editPageDynamicTabTitleFailure)

  const editableInvoiceHeaderFailure = await runTest('编辑页订单头应支持在权限范围内修改分店和供应商', () => {
    assert(editPageSource.includes("import { getActiveLocalSuppliers } from '../../../../services/localSupplierService'"), '编辑页应加载有效本地供应商')
    assert(editPageSource.includes('name="storeCode"') && editPageSource.includes('name="supplierCode"'), '分店和供应商表单值应使用编码')
    assert(editPageSource.includes('options={headerStoreOptions}') && editPageSource.includes('options={headerSupplierOptions}'), '分店和供应商应使用带当前值回显的下拉选项')
    assert(editPageSource.includes('showSearch') && editPageSource.includes('optionFilterProp="label"'), '订单头下拉应支持按编码和名称搜索')
    assert(editPageSource.includes('rules={[{ required: true'), '分店和供应商应执行必填校验')
    assert(editPageSource.includes('isStoreCodeInManagedScope(payload.storeCode, managedStoreCodes)'), '保存前必须复验分店管理范围')
    assert(editPageSource.includes('await updateInvoice(invoiceGuid, payload)'), '保存应提交包含分店和供应商的统一 payload')
    assert(editPageSource.includes('const refreshed = await loadInvoiceAndDetails(false)'), '保存订单头后应读取主表和明细刷新结果')
    assert(editPageSource.includes('if (!refreshed)'), '保存后的刷新失败必须进入安全降级分支')
    const headerSaveStart = editPageSource.indexOf('const handleSave = async () => {')
    const inlineSaveStart = editPageSource.indexOf('const handleInlineDetailSave', headerSaveStart)
    const headerSaveSource = editPageSource.slice(headerSaveStart, inlineSaveStart)
    const refreshFailureStart = headerSaveSource.indexOf('if (!refreshed)')
    const saveSuccessStart = headerSaveSource.indexOf("message.success(t('posAdmin.invoiceDetail.saveSuccess'")
    const refreshFailureSource = headerSaveSource.slice(refreshFailureStart, saveSuccessStart)
    assert(refreshFailureSource.includes('invoiceSnapshotRef.current = null') && refreshFailureSource.includes('detailsSnapshotRef.current = []'), '刷新失败必须清空订单头和明细快照')
    assert(refreshFailureSource.includes('loadedInvoiceGuidRef.current = null') && refreshFailureSource.includes('visibleInvoiceGuidRef.current = null') && refreshFailureSource.includes('lastLoadedManagedStoreCodeKeyRef.current = null'), '刷新失败必须失效订单和权限范围加载标记')
    assert(refreshFailureSource.includes('setInvoice(null)') && refreshFailureSource.includes('setDetails([])') && refreshFailureSource.includes('setSelectedRowKeys([])') && refreshFailureSource.includes('setRowActions({})'), '刷新失败必须清空订单、明细、选择和行操作状态')
    assert(refreshFailureSource.includes('form.resetFields()') && refreshFailureSource.includes('setCanAccessInvoice(false)'), '刷新失败必须清空表单并禁止后续写操作')
    assert(refreshFailureStart < saveSuccessStart, '普通保存成功提示只能在刷新成功后显示')
    assert(refreshFailureSource.includes("'订单已保存但最新数据刷新失败，请重新加载'"), '刷新失败分支必须保留已保存但需重新加载的 warning')
    assert(refreshFailureSource.includes('return'), '刷新失败分支必须在普通成功提示前返回')
    assert(editPageSource.includes('return true') && editPageSource.includes('return false'), '订单头和明细加载函数应返回明确刷新结果')
    assert(editPageSource.includes('currentUser?.stores?.filter((store) => store.isActive !== false)'), '受限用户的分店候选必须排除停用分店')
    assert(editPageSource.includes('!storeOptions.some((option) => option.value === invoice?.storeCode)'), '当前分店不在有效候选时只能通过禁用兜底项回显')
    assert(invoiceHeaderFormSource.includes('storeCode: data.storeCode') && invoiceHeaderFormSource.includes('supplierCode: data.supplierCode'), '订单头映射应保留分店和供应商编码')
    assert(invoiceHeaderFormSource.includes('storeCode: values.storeCode?.trim()') && invoiceHeaderFormSource.includes('supplierCode: values.supplierCode?.trim()'), '保存 payload 应发送清理后的分店和供应商编码')
    assert(invoiceHeaderFormSource.includes('includeCurrentInvoiceHeaderOption'), '选项缺失或加载失败时应保留当前值回显')
  })
  if (editableInvoiceHeaderFailure) failures.push(editableInvoiceHeaderFailure)

  const batchEditPersistFailure = await runTest('编辑页批量编辑应通过批量更新接口持久化 editFields', () => {
    assert(editPageSource.includes('batchUpdateDetails,'), '编辑页应静态导入批量更新明细接口')
    assert(
      editPageSource.includes('await batchUpdateDetails(submittedInvoiceGuid, items, editFields)'),
      '批量编辑应把 editFields 发送到后端，避免自动定价只在本地临时变化',
    )
    assert(editPageSource.includes('applyInvoiceDetailBatchEdit(prev, submittedDetailGuids, editFields)'), '批量编辑确认后应先乐观更新前端当前明细')
    const batchEditStart = editPageSource.indexOf('const handleBatchEdit = async () => {')
    const storePriceStart = editPageSource.indexOf('const openStorePriceModal', batchEditStart)
    const batchEditSource = editPageSource.slice(batchEditStart, storePriceStart)
    assert(batchEditSource.includes("setBatchEditVisible(false)") && batchEditSource.indexOf('setBatchEditVisible(false)') < batchEditSource.indexOf('await batchUpdateDetails(submittedInvoiceGuid, items, editFields)'), '批量编辑应先关闭弹窗，再后台提交后端')
    assert(batchEditSource.includes('setSelectedRowKeys([])') && batchEditSource.indexOf('setSelectedRowKeys([])') < batchEditSource.indexOf('await batchUpdateDetails(submittedInvoiceGuid, items, editFields)'), '批量编辑应先清空选择，再后台提交后端')
    assert(batchEditSource.includes("t('posAdmin.invoiceDetail.batchUpdateSubmitted', '批量更新已提交')"), '批量编辑应提示后台更新已提交')
    assert(!batchEditSource.includes("message.success(t('posAdmin.invoiceDetail.batchUpdateSuccess', '批量更新成功'))\n        await loadDetails()"), '批量编辑成功路径不应等待全量刷新')
    assert(batchEditSource.includes('if (canApplyInvoiceJobResult(currentInvoiceGuidRef.current, submittedInvoiceGuid))'), '批量编辑失败后应只刷新当前进货单')
    assert(batchEditSource.includes('await loadDetails()'), '批量编辑后台失败时应刷新服务端明细')
    assert(
      !editPageSource.includes('await batchUpsertDetails(invoiceGuid, items)\n      // 使用 batchUpdateDetails 的替代方案'),
      '批量编辑不能用空 items 的 batchUpsertDetails 替代批量更新',
    )
  })
  if (batchEditPersistFailure) failures.push(batchEditPersistFailure)

  const batchEditBooleanSwitchFailure = await runTest('新旧进货单批量编辑的自动定价和特殊商品应使用布尔开关', () => {
    const pageSources = [
      ['进货单明细页', editPageSource],
    ] as const

    for (const [pageName, pageSource] of pageSources) {
      const batchEditModalStart = pageSource.indexOf('{/* 批量编辑 Modal')
      const batchEditModalEnd = pageSource.indexOf('</Modal>', batchEditModalStart)
      const batchEditModalSource = pageSource.slice(batchEditModalStart, batchEditModalEnd)

      assert(batchEditModalStart >= 0 && batchEditModalEnd > batchEditModalStart, `${pageName}应保留批量编辑弹窗`)
      assert(
        /name="isAutoPricing"[\s\S]*?valuePropName="checked"[\s\S]*?initialValue=\{false\}[\s\S]*?<Switch/.test(batchEditModalSource),
        `${pageName}自动定价值应使用默认关闭的 Switch`,
      )
      assert(
        /name="isSpecialProduct"[\s\S]*?valuePropName="checked"[\s\S]*?initialValue=\{false\}[\s\S]*?<Switch/.test(batchEditModalSource),
        `${pageName}特殊商品值应使用默认关闭的 Switch`,
      )
      assertEqual(
        (batchEditModalSource.match(/checkedChildren=\{t\('posAdmin\.invoiceDetail\.yes', '是'\)\}/g) ?? []).length,
        2,
        `${pageName}两个布尔开关都应显示“是”文案`,
      )
      assertEqual(
        (batchEditModalSource.match(/unCheckedChildren=\{t\('posAdmin\.invoiceDetail\.no', '否'\)\}/g) ?? []).length,
        2,
        `${pageName}两个布尔开关都应显示“否”文案`,
      )
      assert(batchEditModalSource.includes('name="updateIsAutoPricing" valuePropName="checked"'), `${pageName}应保留自动定价字段选择 Checkbox`)
      assert(batchEditModalSource.includes('name="updateIsSpecialProduct" valuePropName="checked"'), `${pageName}应保留特殊商品字段选择 Checkbox`)
      assert(!/name="isAutoPricing"[\s\S]*?<Select/.test(batchEditModalSource), `${pageName}自动定价值不应继续使用 Select`)
      assert(!/name="isSpecialProduct"[\s\S]*?<Select/.test(batchEditModalSource), `${pageName}特殊商品值不应继续使用 Select`)
      assert(pageSource.includes('isAutoPricing: values.updateIsAutoPricing ? values.isAutoPricing : undefined'), `${pageName}未选择自动定价字段时不应提交值`)
      assert(pageSource.includes('isSpecialProduct: values.updateIsSpecialProduct ? values.isSpecialProduct : undefined'), `${pageName}未选择特殊商品字段时不应提交值`)
    }
  })
  if (batchEditBooleanSwitchFailure) failures.push(batchEditBooleanSwitchFailure)

  const updateToStoreHqFailure = await runTest('更新到分店不混入HQ写入，同步HQ走独立任务并保留独立HQ弹窗', () => {
    const storeModalStart = editPageSource.indexOf('{/* 更新到分店价格 Modal')
    const hqModalStart = editPageSource.indexOf('{/* 更新 HQ 商品 Modal')
    const storeModalSource = editPageSource.slice(storeModalStart, hqModalStart)

    assert(!editPageSource.includes('name="updateHqProduct"'), '更新到分店弹窗不应再包含 updateHqProduct 复选框')
    assert(!editPageSource.includes('confirmUpdateToStorePrices'), '更新到分店不应再包含同时更新 HQ 的二次确认')
    assert(!editPageSource.includes('showUpdateToStoreHqResult'), '更新到分店不应再展示 HQ 混合结果')
    assert(!editPageSource.includes('updateHqProduct:'), '更新到分店请求不应再传递 updateHqProduct')
    assert(editPageSource.includes('hqUpdateForm.validateFields()'), '更新HQ商品应校验独立弹窗表单')
    assert(editPageSource.includes('startUpdateToStorePricesJob(request)'), '更新到分店应提交后台任务')
    assert(editPageSource.includes('getUpdateToStorePricesJob(jobId)'), '更新到分店应轮询后台任务')
    assert(storeModalSource.includes('initialValues={{ updatePurchasePrice: true, syncToHq: canWriteLocalPurchaseToHq }}'), '更新到分店弹窗默认应勾选更新进货价，有写 HQ 权限时默认勾选同时更新 HQ')
    assert(storeModalSource.includes('name="syncToHq"'), '更新到分店弹窗应有「同时更新 HQ 数据库」开关')
    // 同步 HQ 不再混进更新到分店的请求：本地任务完成后另起「更新HQ商品」任务，沿用同样的明细、分店与字段。
    assert(editPageSource.includes('const syncToHq = values.syncToHq === true && canWriteLocalPurchaseToHq'), '同时更新 HQ 必须同时满足勾选与写 HQ 权限')
    assert(/if \(syncToHq\) \{\s*runUpdateHqProductsJob\(\{[\s\S]*?saveSelectedDetails: false/.test(editPageSource), '更新到分店完成后应另起独立 HQ 任务')
    assert(storeModalSource.includes('name="updatePurchasePrice"'), '更新到分店弹窗应保留更新进货价字段开关')
    assert(storeModalSource.includes('name="updateRetailPrice"'), '更新到分店弹窗应保留更新零售价字段开关')
    assert(storeModalSource.includes('name="updateIsAutoPricing"'), '更新到分店弹窗应保留更新自动定价字段开关')
    assert(storeModalSource.includes('name="updateIsSpecialProduct"'), '更新到分店弹窗应保留更新特殊商品字段开关')
    assert(storeModalSource.includes('name="updateDiscountRate"'), '更新到分店弹窗应保留更新折扣率字段开关')
    assert(!storeModalSource.includes('name="purchasePrice"'), '更新到分店弹窗不应提供进货价覆盖值，应按前端明细行写入')
    assert(!storeModalSource.includes('name="retailPrice"'), '更新到分店弹窗不应提供零售价覆盖值，应按前端明细行写入')
    assert(!storeModalSource.includes('name="isAutoPricing"'), '更新到分店弹窗不应提供自动定价覆盖值，应按前端明细行写入')
    assert(!storeModalSource.includes('name="isSpecialProduct"'), '更新到分店弹窗不应提供特殊商品覆盖值，应按前端明细行写入')
    assert(!storeModalSource.includes('name="discountRate"'), '更新到分店弹窗不应提供折扣率覆盖值，应按前端明细行写入')
    assert(editPageSource.includes('const storePriceDetailSet = new Set(request.detailGuids)'), '更新到分店应按选中的前端明细行构建保存范围')
    assert(editPageSource.includes('await batchUpsertDetails(invoiceGuid, buildInvoiceDetailSaveItems(storePriceDetails))'), '更新到分店提交前应先保存前端当前选中明细行')
    assert(
      editPageSource.indexOf('await batchUpsertDetails(invoiceGuid, buildInvoiceDetailSaveItems(storePriceDetails))') <
      editPageSource.indexOf('const job = await startUpdateToStorePricesJob(request)'),
      '更新到分店应先保存前端明细行，再提交后端分店更新任务',
    )
    assert(editPageSource.includes("completedJob.status === 'Failed'"), '更新到分店轮询到 Failed Job 时应展示失败而不是完成')
    assert(!editPageSource.includes('const result = await updateToStorePrices(request)'), '更新到分店不应再直接等待长请求')
    assert(editPageSource.includes('localSupplierInvoiceBatchJobTimeoutTitle'), '批量后台任务轮询超时应展示后台仍在执行提示')
    assert(editPageSource.includes('name="targetStoreCodes"'), '更新HQ商品弹窗应选择目标分店')
    assert(editPageSource.includes('allHqUpdateStoresSelected'), '更新HQ商品目标分店应支持全选选中状态')
    assert(editPageSource.includes('hasPartialHqUpdateStoreSelection'), '更新HQ商品目标分店应支持半选状态')
    assert(editPageSource.includes("hqUpdateForm.setFieldValue('targetStoreCodes', event.target.checked ? allStoreCodes : [])"), '更新HQ商品目标分店全选应写入当前可选分店编码')
    assert(editPageSource.includes("t('posAdmin.invoiceDetail.updateHqProductsTitle'"), '更新HQ商品应有独立弹窗标题')
    assert(editPageSource.includes("t('posAdmin.invoiceDetail.updateHqProductsResultTitle'"), '更新HQ商品应有独立结果弹窗')
  })
  if (updateToStoreHqFailure) failures.push(updateToStoreHqFailure)

  const updateHqAutoPricingValueFailure = await runTest('更新HQ商品自动定价应按明细行值同步', () => {
    const hqModalStart = editPageSource.indexOf('{/* 更新 HQ 商品 Modal')
    const hqModalSource = editPageSource.slice(hqModalStart)

    assert(hqModalSource.includes('name="updateIsAutoPricing"'), '更新HQ商品弹窗应保留更新自动定价字段开关')
    assert(!hqModalSource.includes('name="isAutoPricing"'), '更新HQ商品弹窗不应提供自动定价是/否下拉，应由后端按明细行值写入')
    assert(editPageSource.includes('const selectedDetailSet = new Set(detailGuids)'), '更新HQ商品应按选中的前端明细行构建保存范围')
    assert(editPageSource.includes('await batchUpsertDetails(invoiceGuid, buildInvoiceDetailSaveItems(selectedDetails))'), '更新HQ商品提交前应先保存前端当前选中明细行')
    assert(
      editPageSource.indexOf('await batchUpsertDetails(invoiceGuid, buildInvoiceDetailSaveItems(selectedDetails))') <
      editPageSource.indexOf('const job = await startUpdateHqProductsJob(invoiceGuid'),
      '更新HQ商品应先保存前端明细行，再提交后端HQ更新任务',
    )
  })
  if (updateHqAutoPricingValueFailure) failures.push(updateHqAutoPricingValueFailure)

  const pasteAndCheckJobPageFailure = await runTest('编辑页粘贴和商品检测应提交后台 Job 并轮询完成通知', () => {
    assert(editPageSource.includes('startPasteDetailsJob,'), '编辑页应静态导入粘贴后台任务创建接口')
    assert(editPageSource.includes('getPasteDetailsJob,'), '编辑页应静态导入粘贴后台任务查询接口')
    assert(editPageSource.includes('startCheckProductsJob,'), '编辑页应静态导入商品检测后台任务创建接口')
    assert(editPageSource.includes('getCheckProductsJob,'), '编辑页应静态导入商品检测后台任务查询接口')
    assert(editPageSource.includes('pollPasteDetailsJob'), '编辑页应为粘贴明细提供后台任务轮询 helper')
    assert(editPageSource.includes('pollCheckProductsJob'), '编辑页应为商品检测提供后台任务轮询 helper')
    assert(editPageSource.includes('createHqSyncJobPoller<PasteDetailsJobResult>'), '粘贴明细应复用后台 Job 轮询器')
    assert(editPageSource.includes('createHqSyncJobPoller<CheckProductsJobResult>'), '商品检测应复用后台 Job 轮询器')
    assert(editPageSource.includes('startPasteDetailsJob({'), '粘贴确认应创建后台任务')
    assert(editPageSource.includes('startCheckProductsJob({'), '商品检测应创建后台任务')
    assert(editPageSource.includes('getPasteDetailsJob(') && editPageSource.includes('getPasteDetailsJob(submittedInvoiceGuid, jobId)'), '粘贴任务应按提交时的发票 id 和 jobId 查询最终状态')
    assert(editPageSource.includes('getCheckProductsJob(') && editPageSource.includes('getCheckProductsJob(submittedInvoiceGuid, jobId)'), '商品检测任务应按提交时的发票 id 和 jobId 查询最终状态')
    assert(editPageSource.includes('isMissingBackgroundJobEndpoint(error)'), '后端 job 接口未发布返回 404 时应识别为可兼容场景')
    assert(editPageSource.includes('await pasteDetails({'), '粘贴 job 创建接口 404 时应回退旧同步接口，避免弹窗操作直接失败')
    assert(editPageSource.includes('await checkProducts({'), '商品检测 job 创建接口 404 时应回退旧同步接口，避免商品检测直接失败')
    assert(
      editPageSource.indexOf('await pasteDetails({') > editPageSource.indexOf('isMissingBackgroundJobEndpoint(error)'),
      '粘贴旧同步接口只能作为 job 创建 404 的兼容回退',
    )
    assert(
      editPageSource.indexOf('await checkProducts({') > editPageSource.indexOf('isMissingBackgroundJobEndpoint(error)'),
      '商品检测旧同步接口只能作为 job 创建 404 的兼容回退',
    )
    assert(editPageSource.includes("t('posAdmin.invoiceDetail.pasteJobSubmitted'"), '粘贴任务提交后应提示后台执行')
    assert(editPageSource.includes("t('posAdmin.invoiceDetail.checkProductsJobSubmitted'"), '商品检测任务提交后应提示后台执行')
    assert(editPageSource.includes('canApplyInvoiceJobResult(currentInvoiceGuidRef.current, submittedInvoiceGuid)'), '粘贴任务完成后应确认仍在同一张进货单再刷新')
    assert(editPageSource.includes('canApplyCheckProductsJobResult({'), '商品检测任务完成后应通过 guard 判断是否可写回')
    assert(editPageSource.includes('currentInvoiceGuidRef.current = invoiceGuid'), '当前发票 ref 应在 render 阶段同步更新，避免 useEffect 前的窄窗口竞态')
    assert(editPageSource.includes('activePasteJobIdRef.current = null') && editPageSource.includes('activeCheckProductsJobIdRef.current = null'), '切换进货单时应清空旧后台 job，避免旧任务写回新页面')
    assert(editPageSource.includes("completedJob.status === 'Failed'") && editPageSource.indexOf("completedJob.status === 'Failed'") < editPageSource.indexOf('applyCheckProductsResponse(result)'), '商品检测失败任务不应先合并 result')
    assert(editPageSource.includes('if (checking) return'), '商品检测运行中应阻止重复提交')
    assert(editPageSource.includes('disabled={checking}'), '商品检测按钮在后台任务运行中应禁用')
    assert(editPageSource.includes('setPasteVisible(false)') && editPageSource.includes("setPasteText('')"), '粘贴任务提交成功后应关闭弹窗并清空输入')
    assert(editPageSource.includes('applyCheckProductsResponse(result)'), '商品检测后台完成后应复用结果合并逻辑更新表格')
    assert(editPageSource.includes('await loadDetails()'), '后台任务完成后应刷新明细')
  })
  if (pasteAndCheckJobPageFailure) failures.push(pasteAndCheckJobPageFailure)

  const batchExecuteConfirmFailure = await runTest('批量执行操作应先展示二次确认并突出新建商品数量', () => {
    assert(editPageSource.includes('Modal.confirm({'), '批量执行操作应使用确认框')
    assert(editPageSource.includes('batchExecuteConfirmTitle'), '确认框应有专用标题文案')
    assert(editPageSource.includes('batchExecuteCreateProductNotice'), '确认框应包含新建商品风险提示文案')
    assert(editPageSource.includes('canRunGlobalLocalPurchaseBatchActions'), '批量执行入口应使用与后端全店访问一致的权限条件')
    assert(editPageSource.includes('if (!isAdmin)') && editPageSource.includes('actionConfig[currentAction] || actionConfig[0]'), '行内操作类型设置应要求管理员权限，但非管理员仍可查看当前状态')
    assert(
      editPageSource.includes('onClick: ({ key }) => void handleRowActionChange(record.detailGUID, key)') &&
      editPageSource.includes('isAdmin ? ('),
      '行内操作类型下拉只应对管理员可交互',
    )
    assert(editPageSource.includes('okButtonProps: { danger: previewSnapshot.confirmedCreateProductCount > 0 }'), '存在新建商品时确认按钮应使用确认预览快照的危险态')
    assert(editPageSource.includes('constrainSelectedRowKeysToVisibleDetails(selectedRowKeys, filteredDetails)'), '批量执行前应收敛到当前可见选中明细')
    assertEqual((editPageSource.match(/await batchExecuteActions\(/g) ?? []).length, 1, '批量执行前端应只发起一次 batchExecuteActions 请求')
    assert(editPageSource.includes('detailGuids: snapshot.detailGuids'), '批量执行应把选中明细整体交给后端批量处理')
    assert(!editPageSource.includes('for (const detailGuid of snapshot.detailGuids)'), '批量执行前端不能按 detailGuid 循环拆分请求')
    assert(!editPageSource.includes('snapshot.detailGuids.map(async'), '批量执行前端不能用 map(async) 拆分请求')
    assert(
      editPageSource.indexOf('await updateDetailAction(invoiceGuid, detailGuid, action)') < editPageSource.indexOf('setRowActions((prev) => ({ ...prev, [detailGuid]: action }))'),
      '行操作类型应在服务端更新成功后再更新本地状态',
    )

    const counts = countSelectedBatchExecuteActions(
      ['d1', 'd2', 'd3'],
      [
        { detailGUID: 'd1', activityType: DetailAction.CreateProduct },
        { detailGUID: 'd2', activityType: DetailAction.UpdatePurchasePrice },
        { detailGUID: 'd3', activityType: DetailAction.WaitForOperation },
      ],
      { d2: DetailAction.CreateProduct },
    )
    assertEqual(counts.selectedCount, 3, '确认统计应包含选中条数')
    assertEqual(counts.createProductCount, 2, '确认统计应以 rowActions 覆盖后的操作类型计算新建商品数量')

    const confirmText = buildBatchExecuteConfirmText({
      ...counts,
      labels: {
        title: '确认执行批量操作？',
        content: '将对 {{count}} 条明细执行已设置的操作。',
        createProductNotice: '其中 {{count}} 条会新建商品，请确认货号、条码和名称无误。',
        okText: '确认执行',
        cancelText: '取消',
      },
    })
    assert(confirmText.content.includes('3 条明细'), '确认文案应展示执行条数')
    assert(confirmText.content.includes('2 条会新建商品'), '确认文案应展示新建商品数量')

    const visibleKeys = constrainSelectedRowKeysToVisibleDetails(
      ['d1', 'd2', 'hidden'],
      [
        { detailGUID: 'd1' },
        { detailGUID: 'd2' },
      ],
    )
    assertDeepEqual(visibleKeys.map(String), ['d1', 'd2'], '筛选后隐藏明细不应继续参与批量执行')
  })
  if (batchExecuteConfirmFailure) failures.push(batchExecuteConfirmFailure)

  const editPageStatsFailure = await runTest('明细页应以处理进度条、快捷筛选和更多筛选代替状态统计标签', () => {
    assert(editPageSource.includes("useState<StatusFilterValue<ProductStatusFilter>>('all')"), '明细页应维护商品状态过滤状态')
    assert(editPageSource.includes("useState<BarcodeStatusFilterValue>('all')"), '明细页应维护条码状态过滤状态（含条码异常组合值）')
    assert(editPageSource.includes("useState<ActionTypeFilterValue>('all')"), '明细页应维护操作类型过滤状态')
    assert(editPageSource.includes("useState<DetailProgressBucketFilter>('all')"), '明细页应维护处理进度分段过滤状态')
    assert(editPageSource.includes('getDetailStatusStats(details, rowActions)'), '快捷筛选与更多筛选的数量应基于全部 details 计算')
    assert(editPageSource.includes('getDetailProgressStats(details, rowActions)'), '处理进度应基于全部 details 和当前操作类型计算')
    assert(
      editPageSource.includes('filterInvoiceDetails(filterDetailsByProgressBucket(details, progressBucketFilter, rowActions)'),
      '过滤链应先按处理进度分段，再委托行为级纯函数',
    )
    assert(
      editPageSource.includes('[details, deferredSearchText, priceFilter, productTypeFilter, productStatusFilter, barcodeStatusFilter, actionTypeFilter, rowActions, progressBucketFilter, specialProductFilter]'),
      '过滤结果应依赖搜索、涨跌、商品类型、状态、操作类型、进度和特殊商品过滤，按 AND 叠加',
    )
    assert(
      editPageSource.includes('const deferredSearchText = useDeferredValue(searchText)'),
      '搜索应延后参与过滤，上千行明细时输入框不被整表重渲染卡住',
    )
    assert(editPageSource.includes('DETAIL_PROGRESS_BUCKETS'), '进度条应按固定的五个互斥分段渲染')
    assert(editPageSource.includes("setProgressBucketFilter(selected ? 'all' : bucket)"), '再次点击同一进度分段应取消过滤')
    assert(editPageSource.includes("toggleStatusFilter(productStatusFilter, 'notExists')"), '再次点击主档不存在快捷筛选应取消过滤')
    assert(editPageSource.includes("setBarcodeStatusFilter(barcodeStatusFilter === 'abnormal' ? 'all' : 'abnormal')"), '条码异常快捷筛选应可切换')
    assert(editPageSource.includes('content={moreFiltersContent}'), '商品类型、状态、操作类型、特殊商品应收进更多筛选')
    assert(editPageSource.includes('<ActiveFilterBar items={activeFilterTags} onClearAll={handleClearAllOuterFilters} />'), '已生效条件应复用列表工具栏共用的已生效筛选条')
    assert(editPageSource.includes('handleClearAllOuterFilters'), '页面应提供清空全部过滤条件入口')
    for (const reset of [
      "setSearchText('')",
      "setPriceFilter('all')",
      "setProductStatusFilter('all')",
      "setBarcodeStatusFilter('all')",
      "setProgressBucketFilter('all')",
      "setSpecialProductFilter('all')",
    ]) {
      assert(editPageSource.includes(reset), `清空过滤应执行 ${reset}`)
    }
    assert(editPageSource.includes('getPendingExecutionDetailGuids(details, rowActions)'), '执行全部待执行应按全部明细计算目标行')
    // 待执行拆成「新建商品」与其余操作两组，各自经未保存守卫提交。
    assert(editPageSource.includes('splitCreateProductDetailGuids(pendingExecutionDetailGuids, details, rowActions)'), '执行全部待执行应拆成新建商品与其余操作两组')
    assert(editPageSource.includes('runAfterUnsavedGuard(() => handleBatchExecute(guids, mode))'), '执行全部待执行前应先处理未保存修改')
    assert(!editPageSource.includes('statusStatsTagColors'), '旧的约 21 个状态统计标签不应再出现')
    assert(!editPageSource.includes("t('posAdmin.invoiceDetail.statusStatsTitle'"), '不应再显示旧的状态统计栏标题')
  })
  if (editPageStatsFailure) failures.push(editPageStatsFailure)

  const barcodeMatchModalFailure = await runTest('编辑页条码状态应可点击查看匹配商品', () => {
    const matchedProductColumnsStart = editPageSource.indexOf('const matchedProductColumns')
    const matchedProductColumnsEnd = editPageSource.indexOf('modal.update({', matchedProductColumnsStart)
    assert(matchedProductColumnsStart >= 0 && matchedProductColumnsEnd > matchedProductColumnsStart, '应能定位弹窗匹配商品表格列定义')
    const matchedProductColumnsSource = editPageSource.slice(matchedProductColumnsStart, matchedProductColumnsEnd)

    assert(editPageSource.includes('getProductsByBarcode'), '编辑页应复用按条码查询匹配商品接口')
    assert(editPageSource.includes('getProductById'), '更换匹配商品主档前应先读取完整商品详情')
    assert(editPageSource.includes('updateProduct'), '更换匹配商品主档应复用商品主档更新接口')
    assert(editPageSource.includes('canManagePosProducts'), '更换匹配商品主档应使用商品管理权限控制')
    assert(editPageSource.includes('buildMatchedProductMasterUpdatePayload'), '更换匹配商品主档应通过纯函数构建完整更新 payload')
    assert(editPageSource.includes('showBarcodeMatchedProducts'), '编辑页应提供条码匹配商品弹窗入口')
    assert(editPageSource.includes("width: 'min(1120px, calc(100vw - 32px))'"), '匹配商品弹窗应加宽到 1120（窄屏不超出视口），避免表格出现横向滚动')
    assert(matchedProductColumnsSource.includes('<Space size={8}>'), '「选用」「更换」之间应留出间距，避免误点')
    assert(matchedProductColumnsSource.includes('danger'), '「更换」会改商品主档，应使用危险样式与「选用」区分')
    assert(editPageSource.includes('matchedProductTableScrollX'), '匹配商品表格应启用横向滚动宽度，避免窄屏列内容竖排')
    assert(editPageSource.includes('tableLayout="fixed"'), '匹配商品表格应使用固定布局稳定列宽')
    assert(editPageSource.includes('matchedProductNameCellStyle'), '匹配商品名称列应使用弹窗专用宽度和换行样式')
    assert(editPageSource.includes('handleReplaceMatchedProductMaster'), '弹窗应提供更换所选匹配商品主档的处理函数')
    assert(editPageSource.includes("t('posAdmin.invoiceDetail.barcodeMatchedProductsTitle'"), '弹窗标题应展示当前条码')
    assert(matchedProductColumnsSource.includes("t('posAdmin.invoiceDetail.matchSource', '来源')"), '弹窗表格应显示匹配来源')
    assert(matchedProductColumnsSource.includes("dataIndex: 'supplierName'"), '弹窗表格应显示供应商名称列')
    assert(matchedProductColumnsSource.includes("t('posAdmin.invoiceDetail.replaceProductMaster', '更换货号和供应商')"), '弹窗表格应提供更换货号和供应商操作')
    assert(matchedProductColumnsSource.includes("t('posAdmin.invoiceDetail.replaceProductMasterShort', '更换')"), '弹窗操作列应使用短按钮文案避免挤压')
    assert(matchedProductColumnsSource.includes('canManagePosProducts'), '弹窗操作列应受商品管理权限控制')
    assert(!matchedProductColumnsSource.includes("dataIndex: 'productCode'"), '弹窗表格不应显示商品编码列')
    assert(editPageSource.includes("t('posAdmin.invoiceDetail.replaceProductMasterConfirmTitle', '确认更换匹配商品主档？')"), '更换前应展示确认标题')
    assert(editPageSource.includes("t('posAdmin.invoiceDetail.replaceProductMasterSourceLine'"), '确认内容应展示所选匹配商品当前货号和供应商')
    assert(editPageSource.includes("t('posAdmin.invoiceDetail.replaceProductMasterTargetLine'"), '确认内容应展示当前明细将写入的货号和供应商')
    assert(editPageSource.includes("onClick={openMatchedProducts}"), '条码状态标签应绑定点击事件')
  })
  if (barcodeMatchModalFailure) failures.push(barcodeMatchModalFailure)

  const editPageStatsBehaviorFailure = await runTest('状态统计和过滤应按真实明细行为计算', () => {
    const details: LocalSupplierInvoiceItemDto[] = [
      {
        detailGUID: 'not-detected',
        itemNumber: 'CARD-001',
        barcode: '111',
        productName: 'Birthday Card',
        lastPurchasePrice: 1,
        purchasePrice: 2,
      },
      {
        detailGUID: 'exists-normal',
        itemNumber: 'BAG-002',
        barcode: '222',
        productName: 'Gift Bag',
        storeProductCode: 'STORE-002',
        existingProductCount: 2,
        barcodeStatus: 1,
        barcodeMatchCount: 1,
        lastPurchasePrice: 5,
        purchasePrice: 4,
        activityType: DetailAction.WaitForOperation,
      },
      {
        detailGUID: 'not-exists-no-match',
        itemNumber: 'WRAP-003',
        barcode: '333',
        productName: 'Wrap',
        existingProductCount: 0,
        barcodeStatus: 2,
        barcodeMatchCount: 0,
        lastPurchasePrice: 1,
        purchasePrice: 1.5,
        activityType: DetailAction.UpdatePurchasePrice,
      },
      {
        detailGUID: 'exists-multi-match',
        itemNumber: 'CARD-004',
        barcode: '444',
        productName: 'Birthday Card Multi',
        existingProductCount: 1,
        barcodeStatus: 2,
        barcodeMatchCount: 3,
        lastPurchasePrice: 2,
        purchasePrice: 3,
        activityType: DetailAction.WaitForOperation,
      },
    ]
    const rowActions = {
      'exists-multi-match': DetailAction.CreateProduct,
    }

    assertEqual(getProductStatusFilter(details[0]), 'notDetected', '未检测商品状态应来自空 existingProductCount')
    assertEqual(getProductStatusFilter(details[1]), 'exists', '已存在商品状态应来自 existingProductCount > 0')
    assertEqual(getProductStatusFilter(details[2]), 'notExists', '不存在商品状态应来自 existingProductCount = 0')
    // 本单新品优先于检测快照：再次商品检测会把 existingProductCount 改成 1，仍应显示为本单新品。
    const createdHereDetail = { ...details[1], detailGUID: 'created-here', existingProductCount: 1, isCreatedByThisInvoice: true }
    assertEqual(getProductStatusFilter(createdHereDetail), 'createdHere', '本单新品应来自 isCreatedByThisInvoice，且优先于 existingProductCount')
    assertEqual(getDetailStatusStats([createdHereDetail, details[1]]).product.createdHere, 1, '状态统计应单独计入本单新品')
    assertEqual(getDetailStatusStats([createdHereDetail, details[1]]).product.exists, 1, '本单新品不应同时计入已存在')
    assertEqual(getBarcodeStatusFilter(details[0]), 'notDetected', '未检测条码状态应来自空 barcodeStatus')
    assertEqual(getBarcodeStatusFilter(details[1]), 'normal', '正常条码状态应来自 barcodeStatus = 1')
    assertEqual(getBarcodeStatusFilter(details[2]), 'noMatch', '无匹配条码状态应来自异常且匹配数为 0')
    assertEqual(getBarcodeStatusFilter(details[3]), 'multiMatch', '多匹配条码状态应来自异常且匹配数大于 0')
    assertEqual(getActionTypeFilter(details[0], rowActions), DetailAction.None, '空操作类型应按无统计')
    assertEqual(getActionTypeFilter(details[3], rowActions), DetailAction.CreateProduct, 'rowActions 应覆盖原始操作类型')

    assertDeepEqual(
      getDetailStatusStats(details, rowActions),
      {
        product: { notDetected: 1, exists: 2, notExists: 1, createdHere: 0 },
        barcode: { notDetected: 1, normal: 1, noMatch: 1, multiMatch: 1 },
        action: {
          [DetailAction.None]: 1,
          [DetailAction.CreateProduct]: 1,
          [DetailAction.UpdatePurchasePrice]: 1,
          [DetailAction.WaitForOperation]: 1,
          [DetailAction.UpdateItemNumber]: 0,
          [DetailAction.AddMultiCode]: 0,
        },
      },
      '状态统计应基于全部明细计算',
    )

    const filtered = filterInvoiceDetails(details, {
      searchText: 'card',
      priceFilter: 'up',
      productStatusFilter: 'exists',
      barcodeStatusFilter: 'multiMatch',
      actionTypeFilter: DetailAction.CreateProduct,
      rowActions,
    })
    assertDeepEqual(filtered.map((item) => item.detailGUID), ['exists-multi-match'], '搜索、涨价、商品状态、条码状态、操作类型应按 AND 叠加')
    assertEqual(getDetailStatusStats(filtered, rowActions).product.exists, 1, '过滤结果可单独统计，但页面全量统计不应依赖过滤结果')
    assertEqual(toggleStatusFilter('exists', 'exists'), 'all', '再次点击同一商品状态应取消过滤')
    assertEqual(toggleStatusFilter('all', 'normal'), 'normal', '从全部点击某个条码状态应启用过滤')
    assertEqual(toggleStatusFilter(DetailAction.CreateProduct, DetailAction.CreateProduct), 'all', '再次点击同一操作类型应取消过滤')
  })
  if (editPageStatsBehaviorFailure) failures.push(editPageStatsBehaviorFailure)

  const tableColumnFilterBehaviorFailure = await runTest('明细表列头排序和过滤应只在前端当前数据内生效', () => {
    const details: LocalSupplierInvoiceItemDto[] = [
      {
        detailGUID: 'empty-price',
        itemNumber: 'cap-003',
        barcode: '333',
        productName: 'Cap Birthday',
        purchasePrice: undefined,
        amount: undefined,
        autoPricing: undefined,
        isSpecialProduct: undefined,
        existingProductCount: undefined,
        barcodeStatus: undefined,
      },
      {
        detailGUID: 'cheap-card',
        itemNumber: 'BINE1001',
        barcode: '111',
        productName: 'Birthday Card',
        purchasePrice: 1.2,
        amount: 7.2,
        discountRate: 0.1,
        autoPricing: true,
        isSpecialProduct: false,
        existingProductCount: 1,
        barcodeStatus: 1,
        barcodeMatchCount: 1,
      },
      {
        detailGUID: 'gift-bag',
        itemNumber: 'E11988',
        barcode: '222',
        productName: 'Gift Bag',
        purchasePrice: 1.91,
        amount: 22.92,
        autoPricing: false,
        isSpecialProduct: true,
        existingProductCount: 0,
        barcodeStatus: 2,
        barcodeMatchCount: 0,
        activityType: DetailAction.UpdatePurchasePrice,
      },
    ]

    assertDeepEqual(
      [...details].sort((a, b) => compareNullableNumbers(a.purchasePrice, b.purchasePrice)).map((item) => item.detailGUID),
      ['cheap-card', 'gift-bag', 'empty-price'],
      '数值列升序应按数字排序，并把空值排在最后',
    )
    assertDeepEqual(
      [...details].sort((a, b) => compareNullableText(a.itemNumber, b.itemNumber)).map((item) => item.detailGUID),
      ['cheap-card', 'empty-price', 'gift-bag'],
      '文本列排序应大小写不敏感',
    )
    assertDeepEqual(
      details.filter((item) => matchesTextColumnFilter(item, 'productName', 'birthday')).map((item) => item.detailGUID),
      ['empty-price', 'cheap-card'],
      '列头文本过滤应只匹配指定列',
    )
    assertDeepEqual(
      details.filter((item) => matchesTextColumnFilter(item, 'itemNumber', serializeTextColumnFilter({ mode: 'equals', value: 'BINE1001' }))).map((item) => item.detailGUID),
      ['cheap-card'],
      '文本列应支持等于匹配',
    )
    assertDeepEqual(
      details.filter((item) => matchesTextColumnFilter(item, 'itemNumber', serializeTextColumnFilter({ mode: 'startsWith', value: 'cap' }))).map((item) => item.detailGUID),
      ['empty-price'],
      '文本列应支持开头匹配',
    )
    assertDeepEqual(
      details.filter((item) => matchesTextColumnFilter(item, 'productName', serializeTextColumnFilter({ mode: 'endsWith', value: 'bag' }))).map((item) => item.detailGUID),
      ['gift-bag'],
      '文本列应支持结尾匹配',
    )
    assertDeepEqual(
      details.filter((item) => matchesTextColumnFilter(item, 'barcode', serializeTextColumnFilter({ mode: 'empty' }))).map((item) => item.detailGUID),
      [],
      '文本列应支持为空匹配',
    )
    assertDeepEqual(
      details.filter((item) => matchesTextColumnFilter(item, 'barcode', serializeTextColumnFilter({ mode: 'notEmpty' }))).map((item) => item.detailGUID),
      ['empty-price', 'cheap-card', 'gift-bag'],
      '文本列应支持非空匹配',
    )
    assertDeepEqual(
      details.filter((item) => matchesNumberColumnFilter(item, 'purchasePrice', serializeNumberColumnFilter({ mode: 'gt', value: 1.5 }))).map((item) => item.detailGUID),
      ['gift-bag'],
      '数字列应支持大于匹配',
    )
    assertDeepEqual(
      details.filter((item) => matchesNumberColumnFilter(item, 'purchasePrice', serializeNumberColumnFilter({ mode: 'lt', value: 1.5 }))).map((item) => item.detailGUID),
      ['cheap-card'],
      '数字列应支持小于匹配',
    )
    assertDeepEqual(
      details.filter((item) => matchesNumberColumnFilter(item, 'purchasePrice', serializeNumberColumnFilter({ mode: 'between', min: 1, max: 1.5 }))).map((item) => item.detailGUID),
      ['cheap-card'],
      '数字列应支持区间匹配',
    )
    assertDeepEqual(
      details.filter((item) => matchesNumberColumnFilter(item, 'purchasePrice', serializeNumberColumnFilter({ mode: 'empty' }))).map((item) => item.detailGUID),
      ['empty-price'],
      '数字列应支持为空匹配',
    )
    assertDeepEqual(
      details.filter((item) => matchesNumberColumnFilter(item, 'amount', serializeNumberColumnFilter({ mode: 'notEmpty' }))).map((item) => item.detailGUID),
      ['cheap-card', 'gift-bag'],
      '数字列应支持非空匹配',
    )
    assertDeepEqual(
      details.filter((item) => matchesNumberColumnFilter(item, 'discountRate', serializeNumberColumnFilter({ mode: 'equals', value: 10 }))).map((item) => item.detailGUID),
      ['cheap-card'],
      '折扣率列过滤按页面展示百分比匹配',
    )
    assertDeepEqual(
      details.filter((item) => filterBooleanColumn(item.autoPricing, true)).map((item) => item.detailGUID),
      ['cheap-card'],
      '自动定价列应支持布尔过滤',
    )
    assertDeepEqual(
      details.filter((item) => filterBooleanColumn(item.autoPricing, false)).map((item) => item.detailGUID),
      ['gift-bag'],
      '自动定价 false 过滤不应包含未检测空值',
    )
    assertDeepEqual(
      details.filter((item) => filterBooleanColumn(item.isSpecialProduct, true)).map((item) => item.detailGUID),
      ['gift-bag'],
      '特殊商品列应支持布尔过滤',
    )
    assertDeepEqual(
      details.filter((item) => filterBooleanColumn(item.isSpecialProduct, false)).map((item) => item.detailGUID),
      ['cheap-card'],
      '特殊商品 false 过滤不应包含未检测空值',
    )
    assertDeepEqual(
      details.filter((item) => filterProductStatusColumn(item, 'notExists')).map((item) => item.detailGUID),
      ['gift-bag'],
      '商品状态列过滤应复用商品状态规则',
    )
    assertDeepEqual(
      details.filter((item) => filterBarcodeStatusColumn(item, 'noMatch')).map((item) => item.detailGUID),
      ['gift-bag'],
      '条码状态列过滤应复用条码状态规则',
    )
    assertDeepEqual(
      details.filter((item) => matchesActionTypeColumnFilter(item, DetailAction.UpdatePurchasePrice)).map((item) => item.detailGUID),
      ['gift-bag'],
      '操作类型列过滤应按当前明细操作类型匹配',
    )

    const topFiltered = filterInvoiceDetails(details, {
      searchText: 'gift',
      priceFilter: 'all',
      productStatusFilter: 'notExists',
      barcodeStatusFilter: 'all',
    })
    assertDeepEqual(
      topFiltered.filter((item) => filterBooleanColumn(item.isSpecialProduct, true)).map((item) => item.detailGUID),
      ['gift-bag'],
      '列头过滤应能与顶部搜索和状态筛选按 AND 叠加',
    )

    assert(editPageSource.includes('const [columnFilteredValues, setColumnFilteredValues]'), '明细页应维护受控列过滤状态')
    assert(editPageSource.includes('setColumnFilteredValues(filters as Record<string, (React.Key | boolean)[] | null>)'), '表格变化时应保存列头过滤状态')
    assert(editPageSource.includes('setColumnFilteredValues({})'), '清空过滤应重置列头过滤')
    assert(
      editPageSource.includes("t('posAdmin.invoiceWorkbench.filterColumnCount', { count: activeColumnFilterCount })")
        && editPageSource.includes("source: 'column'"),
      '已生效筛选条应以列来源标签展示列头过滤摘要',
    )
    ;['quantity', 'purchasePrice', 'retailPrice', 'amount'].forEach((field) => {
      assert(editPageSource.includes(`...getNumberColumnFilterProps('${field}'`), `${field} 数字列应挂载列头过滤`)
    })
    assert(editPageSource.includes("...getTextColumnSearchProps('productName'"), '合并后的商品列应挂载商品名称文本过滤')
    assert(editPageSource.includes('onFilter: (value, record) => filterBooleanColumn(record.autoPricing, value)'), '定价列应保留自动/手动过滤')
    const seqColumnSource = editPageSource.slice(
      editPageSource.indexOf("key: 'seq',"),
      editPageSource.indexOf("key: 'productName',"),
    )
    assert(seqColumnSource.length > 0, '应能定位序号列定义')
    assert(!seqColumnSource.includes('onFilter') && !seqColumnSource.includes('filters:') && !seqColumnSource.includes('filteredValue'), '序号列不应挂载列过滤')
  })
  if (tableColumnFilterBehaviorFailure) failures.push(tableColumnFilterBehaviorFailure)

  const compactTableDisplayFailure = await runTest('明细表应合并列并把多出的宽度留给商品列', () => {
    assert(editPageSource.includes('function renderNumericCell'), '明细页应提供数字单元格 helper')
    assert(editPageSource.includes('className="lsi-wb-table"'), '明细表应使用工作台专用 className')
    assert(editPageSource.includes('scroll={{ x: detailTableScrollX, y: tableScrollY }}'), '明细表横向宽度应按可见列宽动态计算')
    assert(editPageSource.includes('fixed: true') && editPageSource.includes('columnWidth: 36'), '选择列应固定在左侧并压缩宽度')
    assert(editPageSource.includes("width: 44,\n      align: 'center',\n      fixed: 'left'"), '序号列应窄而固定在左侧')
    const productColumnSource = editPageSource.slice(
      editPageSource.indexOf("key: 'productName',"),
      editPageSource.indexOf("key: 'quantity',"),
    )
    assert(!/\n      width: \d+,/.test(productColumnSource), '商品列不设宽度，屏幕更宽时多出的空间只给它')
    assert(productColumnSource.includes('width={36} height={36}'), '图片缩略图应压缩到 36px')
    assert(productColumnSource.includes('field="productName"') && productColumnSource.includes('field="itemNumber"') && productColumnSource.includes('field="barcode"'), '商品列应合并名称、货号、条码并保留行内编辑')
    assert(productColumnSource.includes('onSave={handleInlineDetailSave}'), '商品列行内编辑应回到统一保存 handler')
    assert(productColumnSource.includes('content={<BarcodePreview value={record.barcode} compactCopy />}'), '条码图收进弹出层，不再撑高每一行')
    assert(barcodePreviewSource.includes('onClick={handleCopyClick}'), '条码预览复制按钮应保留复制入口')
    assert(barcodePreviewSource.includes('onDoubleClick={stopCopyDoubleClick}'), '条码预览复制按钮双击不应冒泡触发行内编辑')
    assert(barcodePreviewSource.includes('event.stopPropagation()'), '条码预览复制事件应阻止冒泡，避免破坏双击编辑边界')
    assert(productColumnSource.includes('additionalBarcodeCount'), '商品列应显示副码数量标签')
    assert(productColumnSource.includes('record.additionalBarcodes?.join'), '副码数量标签应悬浮显示完整副码列表')
    assert(editPageSource.includes('getPurchasePriceChange(record.lastPurchasePrice, record.purchasePrice)'), '进货价列应合并上次进货价并显示涨跌幅')
    assert(editPageSource.includes('formatPricingFloatRate'), '定价浮率应使用专用两位小数格式化')
    assert(!editPageSource.includes('`${(v * 100).toFixed(1)}%`'), '定价浮率不应按百分比展示')
    assert(!editPageSource.includes('\n          bordered\n'), '明细表不应使用 bordered 边框')
    assert(invoicePageStyleSource.includes('.lsi-num') && invoicePageStyleSource.includes('font-variant-numeric: tabular-nums'), '数字应使用等宽数字视觉')
    assert(invoicePageStyleSource.includes('.lsi-wb-edited'), '未保存的单元格应有醒目的标记样式')
    assert(!globalStyleSource.includes('.invoice-detail-compact-table'), '旧紧凑表格样式已随重设计移出首屏全局样式')
  })
  if (compactTableDisplayFailure) failures.push(compactTableDisplayFailure)

  const inlineBooleanToggleFailure = await runTest('定价字段应在定价弹窗里集中修改并随保存修改统一落库', () => {
    assert(editPageSource.includes("import PricingEditor from './PricingEditor'"), '明细页应接入定价弹窗')
    assert(!editPageSource.includes('EditableBooleanCell'), '自动定价和特殊商品不应再用单击/双击不一致的布尔单元格')
    assert(!editCellsSource.includes('function EditableBooleanCell'), '不再使用的布尔单元格应删除')
    assert(editPageSource.includes('changes.forEach((change) => handleInlineDetailSave(detailGuid, change.field, change.value))'), '定价弹窗的改动应逐项写入本地明细')
    assert(pricingEditorSource.includes('buildPricingEditorChanges(detail, {'), '定价弹窗应只提交改动过的字段')
    assert(pricingEditorChangesSource.includes("changes.push({ field: 'autoPricing', value: draft.autoPricing })"), '自动定价应纳入定价弹窗')
    assert(pricingEditorChangesSource.includes("changes.push({ field: 'isSpecialProduct', value: draft.isSpecialProduct })"), '特殊商品应纳入定价弹窗')
    assert(editPageSource.includes('const handleInlineDetailSave = useCallback'), '行内编辑应先写入本地明细')
    assert(editPageSource.includes('applyInvoiceDetailInlineEdit(prev, detailGuid, field, normalizedValue)'), '行内编辑应复用本地明细更新 helper')
    assert(!editPageSource.includes('handleInlineBooleanToggle'), '布尔字段不应再使用即时落库 handler')
    assert(!editPageSource.includes('inlineBooleanUpdatingKeys'), '布尔字段不应再维护即时保存中的状态')
    assert(
      !editPageSource.includes('await batchUpdateDetails(invoiceGuid, [{ detailGUID: record.detailGUID }], editFields)'),
      '定价修改不应立即调用批量更新接口',
    )
    assert(editPageSource.includes('buildInvoiceDetailSaveItems(details)'), '保存明细应统一构建业务字段 payload')
    assert(editPageSource.includes('await batchUpsertDetails(invoiceGuid, items)'), '保存明细应统一调用 batchUpsertDetails 落库')
    assert(editPageSource.includes('const handleSaveAll = async () => {'), '表头与明细应合并为一个保存入口')
    assert(
      editPageSource.indexOf('await handleSaveDetails({ reload: !headerDirty })') < editPageSource.indexOf('return await handleSave()'),
      '统一保存应先存明细（不刷新），再存表头（表头保存会刷新订单头和明细）',
    )
  })
  if (inlineBooleanToggleFailure) failures.push(inlineBooleanToggleFailure)

  const emptyDiscountRateFailure = await runTest('折扣率空值进入编辑时不应被兜底成 0 落库', () => {
    assert(pricingEditorSource.includes('useState<number | null>(discountRateToPercent(detail.discountRate) ?? null)'), '折扣率编辑值应保留空值')
    assert(!pricingEditorSource.includes('discountRateToPercent(detail.discountRate) ?? 0'), '折扣率空值不能在进入编辑态时被改成 0')
    assert(pricingEditorChangesSource.includes('if (draft.discountPercent != null'), '折扣率留空表示不修改')
  })
  if (emptyDiscountRateFailure) failures.push(emptyDiscountRateFailure)

  const pastePriceParseFailure = await runTest('粘贴解析应识别带货币符号的本次进货价', () => {
    const [row] = parsePasteText('WEW1272\t9313559661518\tFolded Wrap\t15\tA$1.25\t$3.50\tAUD 2.99')

    assertEqual(row.itemNumber, 'WEW1272', '应保留货号')
    assertEqual(row.quantity, 15, '应解析数量')
    assertEqual(row.purchasePrice, 1.25, '应解析带 A$ 的本次进货价')
    assertEqual(row.newAutoRetailPrice, 3.5, '应解析带 $ 的新自动零售价')
    assertEqual(row.retailPrice, 2.99, '应解析带 AUD 的零售价')
  })
  if (pastePriceParseFailure) failures.push(pastePriceParseFailure)

  const pasteFieldOrderFailure = await runTest('粘贴解析应支持弹窗自定义列对应字段', () => {
    const [row] = parsePasteText(
      '9313559661518\tWEW1272\t15\tFolded Wrap\tA$1.25\tAUD 2.99\t$3.50',
      ['barcode', 'itemNumber', 'quantity', 'productName', 'purchasePrice', 'retailPrice', 'newAutoRetailPrice'],
    )

    assertDeepEqual(defaultPasteFieldOrder, ['itemNumber', 'barcode', 'productName', 'quantity', 'purchasePrice', 'newAutoRetailPrice', 'retailPrice'], '默认粘贴列顺序应保持旧顺序')
    assertEqual(row.itemNumber, 'WEW1272', '自定义顺序应解析货号')
    assertEqual(row.barcode, '9313559661518', '自定义顺序应解析条码')
    assertEqual(row.productName, 'Folded Wrap', '自定义顺序应解析商品名称')
    assertEqual(row.quantity, 15, '自定义顺序应解析数量')
    assertEqual(row.purchasePrice, 1.25, '自定义顺序应解析进货价')
    assertEqual(row.retailPrice, 2.99, '自定义顺序应解析零售价')
    assertEqual(row.newAutoRetailPrice, 3.5, '自定义顺序应解析新自动零售价')
  })
  if (pasteFieldOrderFailure) failures.push(pasteFieldOrderFailure)

  const pasteSkipFieldFailure = await runTest('粘贴解析应允许跳过 Excel 多余列', () => {
    const [row] = parsePasteText(
      'WEW1272\t备注列\t9313559661518\tFolded Wrap\t15',
      ['itemNumber', 'skip', 'barcode', 'productName', 'quantity'],
    )

    assertEqual(row.itemNumber, 'WEW1272', '跳过列不应影响后续货号映射')
    assertEqual(row.barcode, '9313559661518', '跳过列不应影响后续条码映射')
    assertEqual(row.productName, 'Folded Wrap', '跳过列不应影响后续商品名称映射')
    assertEqual(row.quantity, 15, '跳过列不应影响后续数量映射')
  })
  if (pasteSkipFieldFailure) failures.push(pasteSkipFieldFailure)

  const pasteSkipExtraColumnFailure = await runTest('粘贴解析跳过多余列后仍应保留全部业务字段', () => {
    const [row] = parsePasteText(
      'WEW1272\t备注列\t9313559661518\tFolded Wrap\t15\tA$1.25\t$3.50\tAUD 2.99',
      ['itemNumber', 'skip', 'barcode', 'productName', 'quantity', 'purchasePrice', 'newAutoRetailPrice', 'retailPrice'],
    )

    assertEqual(row.itemNumber, 'WEW1272', '8 列粘贴应保留货号')
    assertEqual(row.barcode, '9313559661518', '8 列粘贴应保留条码')
    assertEqual(row.productName, 'Folded Wrap', '8 列粘贴应保留商品名称')
    assertEqual(row.quantity, 15, '8 列粘贴应保留数量')
    assertEqual(row.purchasePrice, 1.25, '8 列粘贴应保留进货价')
    assertEqual(row.newAutoRetailPrice, 3.5, '8 列粘贴应保留新自动零售价')
    assertEqual(row.retailPrice, 2.99, '8 列粘贴应保留零售价')
  })
  if (pasteSkipExtraColumnFailure) failures.push(pasteSkipExtraColumnFailure)

  const pasteFieldOrderUiFailure = await runTest('编辑页粘贴弹窗应提供列字段映射并本地记住配置', () => {
    assert(editPageSource.includes('pasteFieldOrder'), '编辑页应维护 pasteFieldOrder 状态')
    assert(editPageSource.includes('hbweb_rv.localSupplierInvoice.pasteFieldOrder.v1'), '编辑页应使用固定 localStorage key 保存列顺序')
    assert(editPageSource.includes('normalizeRetailPriceOnPaste'), '编辑页应维护零售价小数规范化开关')
    assert(editPageSource.includes('parsePasteText(pasteText, pasteFieldOrder, pasteParseOptions)'), '提交和预览应使用当前列字段映射和粘贴解析选项解析')
    assert(editPageSource.includes('pasteMultilineCellMode'), '编辑页应维护多行单元格处理模式')
    assert(editPageSource.includes('pasteMultilineMerge'), '编辑页应提供单元格内合并选项')
    assert(editPageSource.includes('pasteMultilineSmartSplit'), '编辑页应提供按换行智能拆分选项')
    assert(editPageSource.includes('pasteMultilineUnsafeWarning'), '编辑页应提示无法安全拆分的记录会自动合并')
    assert(editPageSource.includes('pasteFieldDuplicateWarning'), '编辑页应提供重复字段校验提示')
    assert(editPageSource.includes('pasteRestoreDefaultOrder'), '编辑页应提供恢复默认列顺序入口')
    assert(editPageSource.includes('pasteFieldSkip'), '编辑页应提供跳过列选项')
    assert(editPageSource.includes('getPasteTextMaxColumnCount'), '编辑页应按粘贴内容列数扩展映射位')
    assert(editPageSource.includes('analyzePasteMultilineCells'), '编辑页应使用共享 helper 分析多行单元格')
    assert(editPageSource.includes("fill('skip')"), '新增的多余列映射应默认设置为跳过')
  })
  if (pasteFieldOrderUiFailure) failures.push(pasteFieldOrderUiFailure)

  const serviceContractFailure = await runTest('服务层应显式识别业务失败并保留 payload', () => {
    assert(serviceSource.includes('ensureHqProducts('), '服务层应导出 ensureHqProducts')
    assert(serviceSource.includes('/details/ensure-hq-products'), '服务层应调用商品级同步到 HQ 接口')
    assert(serviceSource.includes('updateHqProducts('), '服务层应导出字段级更新 HQ 接口')
    assert(serviceSource.includes('/details/update-hq-products'), '服务层应调用字段级更新 HQ 专用接口')
    assert(serviceSource.includes('startUpdateToStorePricesJob('), '服务层应导出更新到分店后台任务提交接口')
    assert(serviceSource.includes('/update-to-store-prices/jobs'), '服务层应调用更新到分店后台任务提交接口')
    assert(serviceSource.includes('getUpdateToStorePricesJob('), '服务层应导出更新到分店后台任务查询接口')
    assert(serviceSource.includes('/update-to-store-prices/jobs/${encodeURIComponent(jobId)}'), '服务层应调用更新到分店后台任务查询接口')
    assert(serviceSource.includes('startUpdateHqProductsJob('), '服务层应导出更新HQ商品后台任务提交接口')
    assert(serviceSource.includes('/details/update-hq-products/jobs'), '服务层应调用更新HQ商品后台任务提交接口')
    assert(serviceSource.includes('getUpdateHqProductsJob('), '服务层应导出更新HQ商品后台任务查询接口')
    assert(serviceSource.includes('/details/update-hq-products/jobs/${encodeURIComponent(jobId)}'), '服务层应调用更新HQ商品后台任务查询接口')
    assert(serviceSource.includes('assertApiSuccess'), '服务层应复用业务失败检查 helper')
    assert(serviceSource.includes("response.success === false || response.isSuccess === false"), '服务层应识别 success false')
    assert(serviceSource.includes("assertApiSuccess(response, '批量执行操作失败')"), '批量执行服务层应识别业务失败')
    assert(serviceSource.includes("assertApiSuccess(response, '保存明细失败')"), '保存明细服务层应识别业务失败')
    assert(serviceSource.includes("assertApiSuccess(response, '批量编辑明细失败')"), '批量编辑明细服务层应识别业务失败')
    assert(serviceSource.includes("assertApiSuccess(response, '批量设置操作类型失败')"), '批量设置操作类型服务层应识别业务失败')
    assert(serviceSource.includes("assertApiSuccess(response, '更新操作类型失败')"), '行操作类型服务层应识别业务失败')
  })
  if (serviceContractFailure) failures.push(serviceContractFailure)

  const originalFetch = globalThis.fetch
  let capturedUrl = ''
  let capturedInit: RequestInit | undefined

  const serviceSuccessFailure = await runTest('syncInvoicesFromHq 应调用页面专用接口并保留 payload', async () => {
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      capturedUrl = String(input)
      capturedInit = init
      return new Response(JSON.stringify({
        success: true,
        data: {
          requestId: 'req-1',
          status: 'Succeeded',
          startedAt: '2026-05-01T00:00:00Z',
          completedAt: '2026-05-01T00:00:01Z',
          durationMs: 1000,
          invoiceAddedCount: 1,
          invoiceUpdatedCount: 2,
          detailAddedCount: 3,
          detailUpdatedCount: 4,
          totalProcessed: 10,
          errors: [],
        },
      }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }) as typeof fetch

    await syncInvoicesFromHq({
      selectedStoreCodes: ['S01'],
      startDate: '2026-05-01',
      endDate: '2026-05-31',
    })

    assertEqual(capturedUrl, '/api/react/v1/local-supplier-invoices/sync-from-hq', '应调用页面专用接口')
    assertEqual(capturedInit?.method, 'POST', '应使用 POST')
    assertDeepEqual(
      JSON.parse(String(capturedInit?.body)),
      { selectedStoreCodes: ['S01'], startDate: '2026-05-01', endDate: '2026-05-31' },
      '应保留同步请求 payload',
    )
  })
  if (serviceSuccessFailure) failures.push(serviceSuccessFailure)

  const serviceFailure = await runTest('syncInvoicesFromHq 遇到业务失败应抛出后端消息', async () => {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      success: false,
      message: 'HQ 同步失败：测试业务错误',
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })) as typeof fetch

    await assertRejects(
      () => syncInvoicesFromHq({ startDate: '2026-05-01' }),
      'HQ 同步失败：测试业务错误',
      '业务失败时应透传后端消息',
    )
  })
  if (serviceFailure) failures.push(serviceFailure)

  const serviceHttpFailure = await runTest('syncInvoicesFromHq 遇到 400 失败应保留统计 payload', async () => {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      success: false,
      message: 'HQ 同步部分失败',
      data: {
        requestId: 'req-failed',
        status: 'Failed',
        startedAt: '2026-05-01T00:00:00Z',
        completedAt: '2026-05-01T00:00:01Z',
        durationMs: 1000,
        invoiceAddedCount: 1,
        invoiceUpdatedCount: 0,
        detailAddedCount: 0,
        detailUpdatedCount: 0,
        totalProcessed: 1,
        errors: ['测试页失败'],
      },
    }), {
      status: 400,
      headers: { 'Content-Type': 'application/json' },
    })) as typeof fetch

    await assertRequestErrorPayload(
      () => syncInvoicesFromHq({ startDate: '2026-05-01' }),
      'HQ 同步部分失败',
      'HTTP 失败时应透传后端消息',
    )
  })
  if (serviceHttpFailure) failures.push(serviceHttpFailure)

  const ensureHqServiceSuccessFailure = await runTest('ensureHqProducts 应调用商品级接口并保留 payload', async () => {
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      capturedUrl = String(input)
      capturedInit = init
      return new Response(JSON.stringify({
        success: true,
        data: {
          total: 2,
          hqExisting: 1,
          hbwebCreated: 1,
          hqCreated: 1,
          hqSynced: 2,
          hqPurchasePricesUpdated: 2,
          skipped: 0,
          failed: 0,
          errors: [],
        },
      }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }) as typeof fetch

    await ensureHqProducts('invoice-1', {
      detailGuids: ['detail-1', 'detail-2'],
      targetStoreCodes: ['1033'],
      idempotencyKey: 'idem-1',
    })

    assertEqual(capturedUrl, '/api/react/v1/local-supplier-invoices/invoice-1/details/ensure-hq-products', '应调用商品级同步到 HQ 接口')
    assertEqual(capturedInit?.method, 'POST', '商品同步到 HQ 应使用 POST')
    assertDeepEqual(
      JSON.parse(String(capturedInit?.body)),
      { detailGuids: ['detail-1', 'detail-2'], targetStoreCodes: ['1033'], idempotencyKey: 'idem-1' },
      '商品同步到 HQ 应保留请求 payload',
    )
  })
  if (ensureHqServiceSuccessFailure) failures.push(ensureHqServiceSuccessFailure)

  const updateToStorePayloadFailure = await runTest('updateToStorePrices 不应传递 updateHqProduct', async () => {
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      capturedUrl = String(input)
      capturedInit = init
      return new Response(JSON.stringify({
        success: true,
        data: {
          inserted: 0,
          updated: 1,
          failed: 0,
        },
      }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }) as typeof fetch

    const result = await updateToStorePrices({
      invoiceGuid: 'invoice-1',
      detailGuids: ['detail-1'],
      targetStoreCodes: ['1033'],
      updateFields: {
        updatePurchasePrice: true,
        updateRetailPrice: false,
        updateIsAutoPricing: false,
        updateIsSpecialProduct: false,
        updateDiscountRate: false,
      },
    })

    assertEqual(capturedUrl, '/api/react/v1/local-supplier-invoices/update-to-store-prices', '更新到分店接口地址应保持不变')
    assertDeepEqual(
      JSON.parse(String(capturedInit?.body)),
      {
        invoiceGuid: 'invoice-1',
        detailGuids: ['detail-1'],
        targetStoreCodes: ['1033'],
        updateFields: {
          updatePurchasePrice: true,
          updateRetailPrice: false,
          updateIsAutoPricing: false,
          updateIsSpecialProduct: false,
          updateDiscountRate: false,
        },
      },
      '更新到分店不应携带 updateHqProduct payload',
    )
    assertEqual(result.updated, 1, '更新到分店结果应保留分店更新统计')
  })
  if (updateToStorePayloadFailure) failures.push(updateToStorePayloadFailure)

  const updateHqProductsPayloadFailure = await runTest('updateHqProducts 应调用字段级更新 HQ 专用接口并保留 payload', async () => {
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      capturedUrl = String(input)
      capturedInit = init
      return new Response(JSON.stringify({
        success: true,
        data: {
          total: 2,
          updated: 2,
          failed: 0,
          hqPurchasePricesUpdated: 2,
          hqRetailPricesUpdated: 2,
          hqAutoPricingUpdated: 0,
          hqSpecialProductsUpdated: 0,
          hqDiscountRatesUpdated: 0,
          errors: [],
        },
      }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }) as typeof fetch

    const result = await updateHqProducts('invoice-1', {
      detailGuids: ['detail-1', 'detail-2'],
      targetStoreCodes: ['1033', '1005'],
      updateFields: {
        updatePurchasePrice: true,
        updateRetailPrice: true,
        updateIsAutoPricing: false,
        updateIsSpecialProduct: false,
        updateDiscountRate: false,
      },
      idempotencyKey: 'hq-update-1',
    })

    assertEqual(capturedUrl, '/api/react/v1/local-supplier-invoices/invoice-1/details/update-hq-products', '应调用字段级更新 HQ 专用接口')
    assertEqual(capturedInit?.method, 'POST', '字段级更新 HQ 应使用 POST')
    assertDeepEqual(
      JSON.parse(String(capturedInit?.body)),
      {
        detailGuids: ['detail-1', 'detail-2'],
        targetStoreCodes: ['1033', '1005'],
        updateFields: {
          updatePurchasePrice: true,
          updateRetailPrice: true,
          updateIsAutoPricing: false,
          updateIsSpecialProduct: false,
          updateDiscountRate: false,
        },
        idempotencyKey: 'hq-update-1',
      },
      '字段级更新 HQ 应保留请求 payload',
    )
    assertEqual(result.hqRetailPricesUpdated, 2, '字段级更新 HQ 应保留零售价更新统计')
  })
  if (updateHqProductsPayloadFailure) failures.push(updateHqProductsPayloadFailure)

  const updateHqProductsConflictJobFailure = await runTest('更新HQ商品遇到运行中任务时应接管 existingJobId', async () => {
    const requests: Array<{ url: string; method: string }> = []
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      const method = init?.method ?? 'GET'
      requests.push({ url, method })

      if (method === 'POST') {
        return new Response(JSON.stringify({
          success: false,
          message: '同一张本地进货单已有同类后台任务正在执行，请等待完成后再提交新的批量写入',
          data: { existingJobId: 'hq-job-running' },
        }), {
          status: 409,
          headers: { 'Content-Type': 'application/json' },
        })
      }

      return new Response(JSON.stringify({
        success: true,
        data: {
          jobId: 'hq-job-running',
          invoiceGuid: 'invoice-1',
          operationId: 'hq-operation-running',
          status: 'Running',
          targetStoreCodes: ['1033'],
        },
      }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }) as typeof fetch

    const job = await startUpdateHqProductsJob('invoice-1', {
      detailGuids: ['detail-1'],
      targetStoreCodes: ['1033'],
      updateFields: {
        updatePurchasePrice: true,
        updateRetailPrice: false,
        updateIsAutoPricing: false,
        updateIsSpecialProduct: false,
        updateDiscountRate: false,
      },
      idempotencyKey: 'new-attempt',
    })

    assertDeepEqual(
      requests,
      [
        {
          url: '/api/react/v1/local-supplier-invoices/invoice-1/details/update-hq-products/jobs',
          method: 'POST',
        },
        {
          url: '/api/react/v1/local-supplier-invoices/invoice-1/details/update-hq-products/jobs/hq-job-running',
          method: 'GET',
        },
      ],
      '冲突后应查询并接管后端返回的 existingJobId',
    )
    assertEqual(job.jobId, 'hq-job-running', '应返回正在运行的原任务供页面继续轮询')
    assertEqual(job.status, 'Running', '接管任务应保留当前运行状态')
  })
  if (updateHqProductsConflictJobFailure) failures.push(updateHqProductsConflictJobFailure)

  const pasteDetailsJobServiceFailure = await runTest('pasteDetails 后台 Job 接口应调用任务创建和查询地址', async () => {
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      capturedUrl = String(input)
      capturedInit = init
      return new Response(JSON.stringify({
        success: true,
        data: {
          jobId: 'paste-job-1',
          invoiceGuid: 'invoice-1',
          operationId: 'paste-op-1',
          status: 'Queued',
          result: { inserted: 1, updated: 0, failed: 0 },
        },
      }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }) as typeof fetch

    const created = await startPasteDetailsJob({
      invoiceGuid: 'invoice-1',
      mode: 'append',
      items: [{ itemNumber: 'SKU-1', quantity: 2, purchasePrice: 1.5 }],
    })

    assertEqual(capturedUrl, '/api/react/v1/local-supplier-invoices/invoice-1/details/paste/jobs', '粘贴明细应调用后台任务创建接口')
    assertEqual(capturedInit?.method, 'POST', '粘贴明细后台任务创建应使用 POST')
    assertDeepEqual(
      JSON.parse(String(capturedInit?.body)),
      { mode: 'append', items: [{ itemNumber: 'SKU-1', quantity: 2, purchasePrice: 1.5 }] },
      '粘贴明细后台任务创建 body 只应包含 mode 和 items',
    )
    assertEqual(created.jobId, 'paste-job-1', '粘贴明细后台任务应返回 jobId')

    await getPasteDetailsJob('invoice-1', 'paste-job-1')
    assertEqual(capturedUrl, '/api/react/v1/local-supplier-invoices/invoice-1/details/paste/jobs/paste-job-1', '粘贴明细应调用后台任务查询接口')
    assertEqual(capturedInit?.method, 'GET', '粘贴明细后台任务查询应使用 GET')
  })
  if (pasteDetailsJobServiceFailure) failures.push(pasteDetailsJobServiceFailure)

  const checkProductsJobServiceFailure = await runTest('checkProducts 后台 Job 接口应调用任务创建和查询地址', async () => {
    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      capturedUrl = String(input)
      capturedInit = init
      return new Response(JSON.stringify({
        success: true,
        data: {
          jobId: 'check-job-1',
          invoiceGuid: 'invoice-1',
          operationId: 'check-op-1',
          status: 'Succeeded',
          result: {
            results: [],
            summary: {
              total: 0,
              productExists: 0,
              productNotExists: 0,
              barcodeNormal: 0,
              barcodeAbnormal: 0,
            },
          },
        },
      }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }) as typeof fetch

    const created = await startCheckProductsJob({
      invoiceGuid: 'invoice-1',
      detailGuids: ['detail-1'],
    })

    assertEqual(capturedUrl, '/api/react/v1/local-supplier-invoices/check-products/jobs', '商品检测应调用后台任务创建接口')
    assertEqual(capturedInit?.method, 'POST', '商品检测后台任务创建应使用 POST')
    assertDeepEqual(
      JSON.parse(String(capturedInit?.body)),
      { invoiceGuid: 'invoice-1', detailGuids: ['detail-1'] },
      '商品检测后台任务创建 body 应保留原检测请求',
    )
    assertEqual(created.jobId, 'check-job-1', '商品检测后台任务应返回 jobId')

    await getCheckProductsJob('invoice-1', 'check-job-1')
    assertEqual(capturedUrl, '/api/react/v1/local-supplier-invoices/invoice-1/check-products/jobs/check-job-1', '商品检测应调用后台任务查询接口')
    assertEqual(capturedInit?.method, 'GET', '商品检测后台任务查询应使用 GET')
  })
  if (checkProductsJobServiceFailure) failures.push(checkProductsJobServiceFailure)

  const batchExecuteBusinessFailure = await runTest('batchExecuteActions 遇到业务失败应抛出后端消息', async () => {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      success: false,
      message: '批量执行业务失败',
      data: {
        createdProducts: 0,
        updatedPurchasePrices: 0,
        updatedItemNumbers: 0,
        addedMultiCodes: 0,
        skipped: 0,
        failed: 1,
        errors: ['测试执行失败'],
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })) as typeof fetch

    await assertRejects(
      () => batchExecuteActions({
        invoiceGuid: 'invoice-1',
        detailGuids: ['detail-1'],
        expectedActions: [
          { detailGuid: 'detail-1', action: 1, activityType: 1 },
        ],
        confirmedCreateProductCount: 1,
        confirmedAt: '2026-06-02T09:30:00.000Z',
      }),
      '批量执行业务失败',
      '批量执行遇到 success=false 时应透传后端消息',
    )
  })
  if (batchExecuteBusinessFailure) failures.push(batchExecuteBusinessFailure)

  const updateDetailActionBusinessFailure = await runTest('updateDetailAction 遇到业务失败应抛出后端消息', async () => {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      success: false,
      message: '更新操作类型业务失败',
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })) as typeof fetch

    await assertRejects(
      () => updateDetailAction('invoice-1', 'detail-1', DetailAction.CreateProduct),
      '更新操作类型业务失败',
      '行操作类型更新遇到 success=false 时应透传后端消息',
    )
  })
  if (updateDetailActionBusinessFailure) failures.push(updateDetailActionBusinessFailure)

  const batchUpdateDetailActionBusinessFailure = await runTest('batchUpdateDetailAction 遇到业务失败应抛出后端消息', async () => {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      success: false,
      message: '批量设置操作类型业务失败',
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })) as typeof fetch

    await assertRejects(
      () => batchUpdateDetailAction('invoice-1', ['detail-1'], DetailAction.CreateProduct),
      '批量设置操作类型业务失败',
      '批量操作类型更新遇到 success=false 时应透传后端消息',
    )
  })
  if (batchUpdateDetailActionBusinessFailure) failures.push(batchUpdateDetailActionBusinessFailure)

  const updateHqProductsFailurePayload = await runTest('updateHqProducts 业务失败应保留 HQ 错误 payload', async () => {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      success: false,
      message: '更新HQ商品部分失败',
      data: {
        total: 2,
        updated: 1,
        failed: 1,
        hqPurchasePricesUpdated: 1,
        hqRetailPricesUpdated: 0,
        errors: [{ detailGuid: 'detail-2', storeCode: '1033', message: '条码多匹配' }],
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })) as typeof fetch

    try {
      await updateHqProducts('invoice-1', {
        detailGuids: ['detail-1', 'detail-2'],
        targetStoreCodes: ['1033'],
        updateFields: {
          updatePurchasePrice: true,
          updateRetailPrice: false,
          updateIsAutoPricing: false,
          updateIsSpecialProduct: false,
          updateDiscountRate: false,
        },
      })
      throw new Error('Expected updateHqProducts to reject')
    } catch (error: any) {
      assertEqual(error.message, '更新HQ商品部分失败', '业务失败应透传后端消息')
      assertEqual(error.payload?.data?.failed, 1, '业务失败应保留 HQ 失败统计')
      assertEqual(error.payload?.data?.errors?.[0]?.message, '条码多匹配', '业务失败应保留 HQ 逐行错误')
    }
  })
  if (updateHqProductsFailurePayload) failures.push(updateHqProductsFailurePayload)

  const ensureHqServiceFailure = await runTest('ensureHqProducts 遇到业务失败应抛出后端消息并保留 payload', async () => {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      success: false,
      message: '同步商品到HQ部分失败',
      data: {
        total: 1,
        hqExisting: 0,
        hbwebCreated: 0,
        hqCreated: 0,
        hqSynced: 0,
        hqPurchasePricesUpdated: 0,
        skipped: 0,
        failed: 1,
        errors: [{ detailGuid: 'detail-1', storeCode: '1033', message: '条码多匹配' }],
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })) as typeof fetch

    try {
      await ensureHqProducts('invoice-1', { detailGuids: ['detail-1'], targetStoreCodes: ['1033'] })
      throw new Error('Expected ensureHqProducts to reject')
    } catch (error: any) {
      assertEqual(error.message, '同步商品到HQ部分失败', '业务失败应透传后端消息')
      assertEqual(error.payload?.data?.failed, 1, '业务失败应保留统计 payload')
      assertEqual(error.payload?.data?.errors?.[0]?.message, '条码多匹配', '业务失败应保留逐行错误')
    }
  })
  if (ensureHqServiceFailure) failures.push(ensureHqServiceFailure)

  const batchUpdateDetailsFailure = await runTest('batchUpdateDetails 遇到业务失败应抛出后端消息', async () => {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      success: false,
      message: '自动定价不能为空',
      code: 'VALIDATION_ERROR',
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })) as typeof fetch

    await assertRejects(
      () => batchUpdateDetails('invoice-1', [{ detailGUID: 'detail-1' }], {
        updatePurchasePrice: false,
        updateRetailPrice: false,
        updateIsAutoPricing: true,
        updateIsSpecialProduct: false,
        updateDiscountRate: false,
        updateAction: false,
      }),
      '自动定价不能为空',
      '批量编辑业务失败时应透传后端消息',
    )
  })
  if (batchUpdateDetailsFailure) failures.push(batchUpdateDetailsFailure)

  const batchUpsertDetailsFailure = await runTest('batchUpsertDetails 遇到业务失败应抛出后端消息', async () => {
    globalThis.fetch = (async () => new Response(JSON.stringify({
      success: false,
      message: '保存明细业务失败',
      code: 'VALIDATION_ERROR',
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })) as typeof fetch

    await assertRejects(
      () => batchUpsertDetails('invoice-1', [{ detailGUID: 'detail-1', purchasePrice: 1.23 }]),
      '保存明细业务失败',
      '保存明细业务失败时应透传后端消息',
    )
  })
  if (batchUpsertDetailsFailure) failures.push(batchUpsertDetailsFailure)

  globalThis.fetch = originalFetch

  if (failures.length > 0) {
    throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  }

  console.log('LocalSupplierInvoices.hqSync.logic.test: ok')
}

await main()
