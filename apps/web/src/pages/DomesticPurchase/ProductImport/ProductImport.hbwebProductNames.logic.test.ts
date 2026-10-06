import { readFileSync } from 'node:fs'
import { updateHbwebProductNames } from '../../../services/domesticProductImportService'
import type { ProductImportItem } from './types'
import { buildHbwebProductNameSyncNotificationDecision, buildHbwebProductNameUpdates, getHbwebProductNameSyncConfirmationKeys, summarizeHbwebProductNameSyncResponse } from './utils'

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)

  if (actualJson !== expectedJson) {
    throw new Error(`${label}. Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

function createProduct(overrides: Omit<Partial<ProductImportItem>, 'newProduct'> & { newProduct?: Partial<ProductImportItem['newProduct']> }): ProductImportItem {
  const base: ProductImportItem = {
    id: 'row-1',
    selected: true,
    imageUrl: '',
    customImage: false,
    imageLoadStatus: 'success',
    newProduct: {
      quantity: 1,
      productCode: 'HB001',
      productName: '测试商品',
      englishName: 'TEST PRODUCT',
    },
    status: 'unchanged',
    isDuplicate: false,
    calculated: { totalProducts: 1, totalVolume: 0 },
  }

  return {
    ...base,
    ...overrides,
    newProduct: {
      ...base.newProduct,
      ...overrides.newProduct,
    },
  }
}

assertDeepEqual(
  buildHbwebProductNameUpdates([
    createProduct({ id: 'row-1', newProduct: { productCode: ' HB001 ', englishName: ' TEST PRODUCT ' } }),
    createProduct({ id: 'row-2', newProduct: { productCode: 'HB002', englishName: 'SECOND PRODUCT' } }),
    createProduct({ id: 'row-3', newProduct: { productCode: 'HB003', englishName: 'UNSELECTED PRODUCT' } }),
  ], ['row-1', 'row-2'], ' SUP-A '),
  {
    products: [
      { SupplierCode: 'SUP-A', ItemNumber: 'HB001', ProductName: 'TEST PRODUCT' },
      { SupplierCode: 'SUP-A', ItemNumber: 'HB002', ProductName: 'SECOND PRODUCT' },
    ],
    missingItemNumbers: [],
    missingProductNames: [],
    conflictItemNumbers: [],
  },
  '应只用选中行英文名称构建 HBweb Product.ProductName 更新 payload',
)

assertDeepEqual(
  buildHbwebProductNameUpdates([
    createProduct({ id: 'missing-name', newProduct: { productCode: 'HB004', englishName: '   ' } }),
    createProduct({ id: 'missing-item', newProduct: { productCode: '   ', englishName: 'HAS NAME' } }),
  ], ['missing-name', 'missing-item'], 'SUP-A'),
  {
    products: [],
    missingItemNumbers: ['missing-item'],
    missingProductNames: ['HB004'],
    conflictItemNumbers: [],
  },
  '应拦截缺货号和缺英文名称的选中行',
)

assertDeepEqual(
  buildHbwebProductNameUpdates([
    createProduct({ id: 'same-1', newProduct: { productCode: 'hb005', englishName: 'SAME NAME' } }),
    createProduct({ id: 'same-2', newProduct: { productCode: 'HB005', englishName: 'SAME NAME' } }),
  ], ['same-1', 'same-2'], ' sup-a '),
  {
    products: [{ SupplierCode: 'sup-a', ItemNumber: 'hb005', ProductName: 'SAME NAME' }],
    missingItemNumbers: [],
    missingProductNames: [],
    conflictItemNumbers: [],
  },
  '供应商和货号大小写等价且英文名称相同时应按复合键去重',
)

assertDeepEqual(
  buildHbwebProductNameUpdates([
    createProduct({ id: 'conflict-1', newProduct: { productCode: 'HB006', englishName: 'NAME A' } }),
    createProduct({ id: 'conflict-2', newProduct: { productCode: 'HB006', englishName: 'NAME B' } }),
  ], ['conflict-1', 'conflict-2'], 'SUP-A').conflictItemNumbers,
  ['HB006'],
  '同货号不同英文名称应报告冲突',
)

assertDeepEqual(
  summarizeHbwebProductNameSyncResponse({
    success: true,
    data: {
      updatedCount: 2,
      unchangedCount: 1,
      missingItemNumbers: ['HB003'],
      errors: [],
      hqSyncResult: {
        success: true,
        updatedCount: 1,
        unchangedCount: 1,
        missingItemNumbers: ['HB003'],
        errors: ['HQ 重复货号已跳过: HB004'],
      },
    },
  }),
  {
    status: 'success',
    hbweb: { updatedCount: 2, unchangedCount: 1, missingCount: 1, skippedCount: 0 },
    hq: { success: true, updatedCount: 1, unchangedCount: 1, missingCount: 1 },
    hqWarningCount: 1,
  },
  'HQ 同步成功时应提供本地化提示所需统计，不暴露原始 HQ 错误',
)

assertDeepEqual(
  summarizeHbwebProductNameSyncResponse({
    success: false,
    errorCode: 'HQ_PRODUCT_NAME_SYNC_FAILED',
    message: 'HQ 数据库连接失败',
    data: {
      updatedCount: 3,
      unchangedCount: 2,
      missingItemNumbers: ['HB099'],
      errors: ['HBweb 重复货号已跳过: HB098'],
      hqSyncResult: {
        success: false,
        updatedCount: 0,
        unchangedCount: 0,
        missingItemNumbers: [],
        errors: ['HQ 写入失败'],
      },
    },
  }),
  {
    status: 'hqPartialFailure',
    hbweb: { updatedCount: 3, unchangedCount: 2, missingCount: 1, skippedCount: 1 },
    hq: { success: false, updatedCount: 0, unchangedCount: 0, missingCount: 0 },
    hqWarningCount: 1,
  },
  'HQ 部分失败应提供单条 warning 所需的完整 HBweb 统计和通用 HQ error 状态',
)

const partialWithoutDetails = summarizeHbwebProductNameSyncResponse({
    success: false,
    errorCode: 'HQ_PRODUCT_NAME_SYNC_FAILED',
    message: 'HQ 同步暂时不可用',
    data: {
      updatedCount: 1,
      unchangedCount: 0,
      missingItemNumbers: [],
      errors: [],
      hqSyncResult: {
        success: false,
        updatedCount: 0,
        unchangedCount: 0,
        missingItemNumbers: [],
        errors: [],
      },
    },
  })
assertEqual('hqErrors' in partialWithoutDetails, false, '展示模型不得暴露后端 HQ 错误文本')
assertEqual(partialWithoutDetails.hqWarningCount, 0, '没有 HQ 错误明细时警告数量应为零')

assertEqual(
  summarizeHbwebProductNameSyncResponse({
    success: false,
    errorCode: 'INVALID_HBWEB_PRODUCT_NAMES',
    message: '请求无效',
  }).status,
  'failure',
  '其它业务失败应保持普通整体失败行为',
)

assertDeepEqual(
  buildHbwebProductNameSyncNotificationDecision({
    status: 'success',
    hbweb: { updatedCount: 2, unchangedCount: 1, missingCount: 0, skippedCount: 0 },
    hqWarningCount: 0,
  }),
  {
    level: 'success',
    includesHq: false,
    partial: false,
    hbweb: { updatedCount: 2, unchangedCount: 1, missingCount: 0, warningCount: 0 },
  },
  '仅 HBweb 全成功时应选择单条 success 通知',
)

assertDeepEqual(
  buildHbwebProductNameSyncNotificationDecision({
    status: 'success',
    hbweb: { updatedCount: 1, unchangedCount: 0, missingCount: 0, skippedCount: 2 },
    hqWarningCount: 0,
  }),
  {
    level: 'warning',
    includesHq: false,
    partial: false,
    hbweb: { updatedCount: 1, unchangedCount: 0, missingCount: 0, warningCount: 2 },
  },
  'HBweb 有跳过时应选择单条 warning 通知',
)

assertDeepEqual(
  buildHbwebProductNameSyncNotificationDecision({
    status: 'success',
    hbweb: { updatedCount: 2, unchangedCount: 0, missingCount: 0, skippedCount: 0 },
    hq: { success: true, updatedCount: 1, unchangedCount: 1, missingCount: 0 },
    hqWarningCount: 0,
  }),
  {
    level: 'success',
    includesHq: true,
    partial: false,
    hbweb: { updatedCount: 2, unchangedCount: 0, missingCount: 0, warningCount: 0 },
    hq: { updatedCount: 1, unchangedCount: 1, missingCount: 0, warningCount: 0 },
  },
  'HBweb 与 HQ 全成功时应选择单条 success 并包含两端统计',
)

for (const [missingCount, warningCount, label] of [
  [1, 0, 'HQ 有缺失'],
  [0, 2, 'HQ 有错误'],
] as const) {
  assertEqual(
    buildHbwebProductNameSyncNotificationDecision({
      status: 'success',
      hbweb: { updatedCount: 1, unchangedCount: 0, missingCount: 0, skippedCount: 0 },
      hq: { success: true, updatedCount: 0, unchangedCount: 0, missingCount },
      hqWarningCount: warningCount,
    }).level,
    'warning',
    `${label}时应选择单条 warning 通知`,
  )
}

assertDeepEqual(
  buildHbwebProductNameSyncNotificationDecision({
    status: 'hqPartialFailure',
    hbweb: { updatedCount: 3, unchangedCount: 1, missingCount: 1, skippedCount: 2 },
    hq: { success: false, updatedCount: 0, unchangedCount: 0, missingCount: 0 },
    hqWarningCount: 1,
  }),
  {
    level: 'warning',
    includesHq: true,
    partial: true,
    hbweb: { updatedCount: 3, unchangedCount: 1, missingCount: 1, warningCount: 2 },
  },
  'HQ 部分失败时应只保留 HBweb 统计和 HQ 失败标记，产出单条 warning',
)

assertDeepEqual(
  getHbwebProductNameSyncConfirmationKeys(false),
  {
    confirmKey: 'productImport.updateHbwebProductNamesConfirm',
    scopeKey: 'productImport.updateHbwebProductNamesScope',
  },
  '未勾选 HQ 同步时应使用原有确认文案',
)

assertDeepEqual(
  getHbwebProductNameSyncConfirmationKeys(true),
  {
    confirmKey: 'productImport.updateHbwebProductNamesWithHqConfirm',
    scopeKey: 'productImport.updateHbwebProductNamesWithHqScope',
  },
  '勾选 HQ 同步时应使用 HQ 范围确认文案',
)

const pageSource = readFileSync('src/pages/DomesticPurchase/ProductImport/index.tsx', 'utf8')
const zhLocaleSource = readFileSync('src/i18n/locales/zh.json', 'utf8')
const enLocaleSource = readFileSync('src/i18n/locales/en.json', 'utf8')
const zhLocale = JSON.parse(zhLocaleSource)
const enLocale = JSON.parse(enLocaleSource)

assertEqual('unknownError' in zhLocale.productImport, false, '中文 locale 不应保留未使用的 unknownError')
assertEqual('unknownError' in enLocale.productImport, false, '英文 locale 不应保留未使用的 unknownError')
assertEqual(
  zhLocale.productImport?.hqProductNameSyncPartialFailed,
  'HBweb 商品主表名称已更新：更新 {{updated}}，无变化 {{unchanged}}，未找到 {{missing}}，跳过 {{skipped}}；HQ 同步失败',
  '中文部分成功 warning 应在一条消息中包含 HBweb 完整统计',
)
assertEqual(
  enLocale.productImport?.hqProductNameSyncPartialFailed,
  'HBweb product master names were updated: {{updated}} updated, {{unchanged}} unchanged, {{missing}} missing, {{skipped}} skipped; HQ synchronization failed',
  '英文部分成功 warning 应在一条消息中包含 HBweb 完整统计',
)
assertEqual('hqProductNameSyncFailed' in zhLocale.productImport, false, '中文 locale 不应保留已移除的 partial error 文案')
assertEqual('hqProductNameSyncFailed' in enLocale.productImport, false, '英文 locale 不应保留已移除的 partial error 文案')
assertEqual(zhLocale.productImport?.updateHbwebProductNamesSkipped, '跳过商品：{{count}}', '中文 HBweb warning 应使用中性数量文案')
assertEqual(enLocale.productImport?.updateHbwebProductNamesSkipped, 'Skipped items: {{count}}', '英文 HBweb warning 应避免单复数问题')
assertEqual(zhLocale.productImport?.hqProductNameSyncWarning, 'HQ 警告：{{count}}', '中文 HQ warning 应使用中性数量文案')
assertEqual(enLocale.productImport?.hqProductNameSyncWarning, 'HQ warnings: {{count}}', '英文 HQ warning 应使用中性数量文案')

const updateHandlerIndex = pageSource.indexOf('const handleUpdateHbwebProductNames = useCallback')
const updateSupplierGuardIndex = pageSource.indexOf(
  "if (!state.supplier?.trim()) { message.warning(t('productImport.selectSupplierFirst', '请先选择供应商')); return }",
  updateHandlerIndex,
)
const updatePayloadBuilderIndex = pageSource.indexOf(
  'buildHbwebProductNameUpdates(state.products, state.selectedIds, state.supplier)',
  updateHandlerIndex,
)
assertEqual(
  updateHandlerIndex >= 0 && updateSupplierGuardIndex > updateHandlerIndex && updatePayloadBuilderIndex > updateSupplierGuardIndex,
  true,
  '更新主表名称应先复用选择供应商提示拦截空 supplier，再用 state.supplier 构建请求项',
)

assertDeepEqual(
  [
    pageSource.includes('const [syncProductNamesToHq, setSyncProductNamesToHq] = useState(false)'),
    pageSource.includes('checked={syncProductNamesToHq}'),
    pageSource.includes('onChange={(event) => setSyncProductNamesToHq(event.target.checked)}'),
    pageSource.includes('buildHbwebProductNameUpdates(state.products, state.selectedIds, state.supplier)'),
    pageSource.includes('updateHbwebProductNames({ Products: updatePayload.products, SyncToHq: syncProductNamesToHq })'),
  ],
  [true, true, true, true, true],
  '页面应拦截空供应商、从 state.supplier 构建复合键，并保留受控 HQ 同步状态',
)

assertDeepEqual(
  [
    pageSource.includes('buildHbwebProductNameSyncNotificationDecision(feedback)'),
    pageSource.includes("if (notification.level === 'success')"),
    pageSource.includes('message.success(notificationText)'),
    pageSource.includes('message.warning(notificationText)'),
    pageSource.includes('response.data?.errors'),
    pageSource.includes("message.error(t('productImport.hqProductNameSyncFailed'"),
  ],
  [true, true, true, true, false, false],
  '页面终态通知应为 success/warning 二选一，且不得读取原始 errors 或为 partial 追加 error',
)

// 原断言：「更新主表名称」按钮与 HQ 复选框组成不换行分组，并整体位于「检测匹配」按钮之前。
// 重设计后的流程是 检测 → 入库：复选框所在的不换行分组改放在「重新检测」之后、「更新 / 新建」之前；
// 「更新主表名称」按钮收进「更多操作」菜单，仍调用同一个 handleUpdateHbwebProductNames（请求体、确认文案、通知逻辑都不变）。
const detectButtonIndex = pageSource.indexOf("t('productImport.detectMatch', '检测匹配')")
const updateGroupIndex = pageSource.indexOf('<Space size="small" wrap={false}>')
const syncCheckboxIndex = pageSource.indexOf('checked={syncProductNamesToHq}')
const updateGroupEndIndex = pageSource.indexOf('</Space>', syncCheckboxIndex)
const updateCountButtonIndex = pageSource.indexOf("t('productImport.updateCountButton'")
const createCountButtonIndex = pageSource.indexOf("t('productImport.createCountButton'")
assertEqual(
  detectButtonIndex >= 0
    && detectButtonIndex < updateGroupIndex
    && updateGroupIndex < syncCheckboxIndex
    && syncCheckboxIndex < updateGroupEndIndex
    && updateGroupEndIndex < updateCountButtonIndex
    && updateCountButtonIndex < createCountButtonIndex,
  true,
  'HQ 复选框应是不换行分组，位于重新检测按钮之后、更新 / 新建按钮之前',
)
assertDeepEqual(
  [
    pageSource.includes("key: 'updateNames'"),
    pageSource.includes("if (key === 'updateNames') handleUpdateHbwebProductNames()"),
    pageSource.includes("t('productImport.menuUpdateNamesWithHq'"),
    pageSource.includes("t('productImport.menuUpdateNames'"),
  ],
  [true, true, true, true],
  '「更新主表名称」应在更多操作菜单里，并随 HQ 复选框切换文案、调用同一个处理器',
)

const originalFetch = globalThis.fetch
let capturedUrl = ''
let capturedInit: RequestInit | undefined
let nextPayload: unknown = {}
let nextStatus = 200

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  capturedUrl = String(input)
  capturedInit = init

  return new Response(JSON.stringify(nextPayload), {
    status: nextStatus,
    headers: { 'Content-Type': 'application/json' },
  })
}) as typeof fetch

try {
  nextPayload = {
    success: true,
    data: {
      updatedCount: 1,
      unchangedCount: 0,
      missingItemNumbers: [],
      errors: [],
    },
    message: 'ok',
  }

  const response = await updateHbwebProductNames({
    Products: [{ SupplierCode: 'SUP-A', ItemNumber: 'HB001', ProductName: 'TEST PRODUCT' }],
    SyncToHq: false,
  })

  assertEqual(capturedUrl, '/api/react/v1/domestic-products/product-master-names', '服务应调用国内商品主表名称更新接口')
  assertEqual(capturedInit?.method, 'PUT', '服务应使用 PUT')
  assertDeepEqual(
    JSON.parse(String(capturedInit?.body)),
    { Products: [{ SupplierCode: 'SUP-A', ItemNumber: 'HB001', ProductName: 'TEST PRODUCT' }], SyncToHq: false },
    '服务应显式发送 false 并保留 PascalCase 请求体',
  )
  assertEqual(response.data?.updatedCount, 1, '服务应返回更新数量')

  nextPayload = {
    success: true,
    data: {
      updatedCount: 1,
      unchangedCount: 0,
      missingItemNumbers: [],
      errors: [],
      hqSyncResult: {
        success: true,
        updatedCount: 1,
        unchangedCount: 0,
        missingItemNumbers: [],
        errors: [],
      },
    },
  }

  const hqResponse = await updateHbwebProductNames({
    Products: [{ SupplierCode: 'SUP-A', ItemNumber: 'HB001', ProductName: 'TEST PRODUCT' }],
    SyncToHq: true,
  })

  assertDeepEqual(
    JSON.parse(String(capturedInit?.body)),
    { Products: [{ SupplierCode: 'SUP-A', ItemNumber: 'HB001', ProductName: 'TEST PRODUCT' }], SyncToHq: true },
    '服务应显式发送 true 和 SupplierCode',
  )
  assertEqual(hqResponse.data?.hqSyncResult?.updatedCount, 1, '服务应保留 HQ 同步结果')

  nextStatus = 400
  nextPayload = {
    success: false,
    errorCode: 'INVALID_HBWEB_PRODUCT_NAMES',
    message: '存在无效货号或商品名称，请先修正后再更新',
    data: {
      updatedCount: 0,
      unchangedCount: 0,
      missingItemNumbers: [],
      errors: ['商品名称不能为空: HB002'],
    },
  }

  const failedResponse = await updateHbwebProductNames({
    Products: [{ SupplierCode: 'SUP-A', ItemNumber: 'HB002', ProductName: '' }],
  })

  assertEqual(failedResponse.success, false, '服务应保留后端 400 业务失败标记')
  assertEqual(failedResponse.errorCode, 'INVALID_HBWEB_PRODUCT_NAMES', '服务应保留后端错误码')
  assertDeepEqual(failedResponse.data?.errors, ['商品名称不能为空: HB002'], '服务应保留后端错误明细')
} finally {
  globalThis.fetch = originalFetch
}
