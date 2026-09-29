import { readFileSync } from 'node:fs'
import path from 'node:path'
import type { CurrentUser } from '../../../types/auth'
import type { ProductStoreRecordDto } from '../../../types/posProduct'
import { buildAccess } from '../../../utils/access'
import {
  compareProductStoreRecordsByActive,
  compareProductStoreRecordsByAutoPricing,
  compareProductStoreRecordsByDiscountRate,
  compareProductStoreRecordsByName,
  compareProductStoreRecordsByPurchasePrice,
  compareProductStoreRecordsByRetailPrice,
  compareProductStoreRecordsBySpecialProduct,
  compareProductStoreRecordsByStoreCode,
  compareProductStoreRecordsByStoreProductCode,
  compareProductStoreRecordsByUpdatedAt,
  compareProductStoreRecordsByUpdatedBy,
} from './storeRecordSorting'
import {
  buildProductIntegrityFixSummary,
  buildProductIntegritySummary,
} from './productIntegrityReport'

function createCurrentUser(overrides: Partial<CurrentUser> = {}): CurrentUser {
  return {
    userGUID: 'test-user-guid',
    username: 'tester',
    email: 'tester@example.com',
    permissions: [],
    roleNames: [],
    storeNames: [],
    ...overrides,
  }
}

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

const pageFile = path.resolve(process.cwd(), 'src/pages/PosAdmin/ProductManagement/index.tsx')
const typeFile = path.resolve(process.cwd(), 'src/types/posProduct.ts')
const productIntegrityTypeFile = path.resolve(process.cwd(), 'src/types/productIntegrity.ts')
const serviceFile = path.resolve(process.cwd(), 'src/services/posProductService.ts')
const productIntegrityHelperFile = path.resolve(process.cwd(), 'src/pages/PosAdmin/ProductManagement/productIntegrityReport.ts')
const globalStyleFile = path.resolve(process.cwd(), 'src/styles/global.css')
const zhLocaleFile = path.resolve(process.cwd(), 'src/i18n/locales/zh.json')
const enLocaleFile = path.resolve(process.cwd(), 'src/i18n/locales/en.json')
const pageSource = readFileSync(pageFile, 'utf8')
const typeSource = readFileSync(typeFile, 'utf8')
const productIntegrityTypeSource = readFileSync(productIntegrityTypeFile, 'utf8')
const serviceSource = readFileSync(serviceFile, 'utf8')
const productIntegrityHelperSource = readFileSync(productIntegrityHelperFile, 'utf8')
const globalStyleSource = readFileSync(globalStyleFile, 'utf8')
const zhLocaleSource = readFileSync(zhLocaleFile, 'utf8')
const enLocaleSource = readFileSync(enLocaleFile, 'utf8')

function assertSourceOrder(source: string, first: string, second: string, message: string) {
  const firstIndex = source.indexOf(first)
  const secondIndex = source.indexOf(second)

  assert(firstIndex >= 0, `${message}：缺少 ${first}`)
  assert(secondIndex >= 0, `${message}：缺少 ${second}`)
  assert(firstIndex < secondIndex, message)
}

async function main() {
  const failures: string[] = []

  const adminAccessFailure = await runTest('Admin 权限判断成立', () => {
    const access = buildAccess(createCurrentUser({ roleNames: ['Admin'] }))
    assertEqual(access.isAdmin, true, 'Admin 应被识别为管理员')
  })
  if (adminAccessFailure) failures.push(adminAccessFailure)

  const warehouseAccessFailure = await runTest('WarehouseStaff 权限判断成立', () => {
    const access = buildAccess(createCurrentUser({ roleNames: ['WarehouseStaff'] }))
    assertEqual(access.isAdmin, false, 'WarehouseStaff 不应被识别为管理员')
    assertEqual(access.isWarehouseStaff, true, 'WarehouseStaff 应被识别为仓库员工')
  })
  if (warehouseAccessFailure) failures.push(warehouseAccessFailure)

  const productIntegritySummaryFailure = await runTest('商品一致性检查应把后端分组报告转换成页面问题行', () => {
    const summary = buildProductIntegritySummary({
      checkTime: '2026-06-15T00:00:00Z',
      durationSeconds: 1.25,
      productSetCodeReport: {
        tableName: 'ProductSetCode',
        totalChecked: 10,
        orphanedCount: 2,
        missingCount: 0,
        invalidKeyCount: 0,
        orphanedProductCodes: ['P001', 'P002'],
        missingProductCodes: [],
        errors: [],
      },
      storeReports: [
        {
          storeCode: 'S1',
          storeName: 'Sunnybank',
          tableReports: [
            {
              tableName: 'StoreRetailPrice',
              totalChecked: 100,
              orphanedCount: 0,
              missingCount: 3,
              invalidKeyCount: 0,
              orphanedProductCodes: [],
              missingProductCodes: ['P100', 'P101'],
              errors: [],
            },
            {
              tableName: 'StoreMultiCodeProduct',
              totalChecked: 8,
              orphanedCount: 1,
              missingCount: 4,
              invalidKeyCount: 0,
              orphanedProductCodes: ['P200'],
              missingProductCodes: ['P201', 'P202'],
              errors: [],
            },
          ],
        },
      ],
    })

    assertEqual(summary.storeCount, 1, '应统计检查分店数量')
    assertEqual(summary.totalChecked, 118, '应汇总总部和分店表的检查记录数')
    assertEqual(summary.issueCount, 10, '问题数应为孤立记录和缺失记录合计')
    assertEqual(summary.issueRows.length, 4, '应按范围、表名和问题类型生成问题行')
    assert(summary.issueRows.some((row) =>
      row.scope === '总部' &&
      row.tableName === 'ProductSetCode' &&
      row.issueType === '孤立记录' &&
      row.count === 2 &&
      row.sampleProductCodes.includes('P001'),
    ), 'ProductSetCode 孤立记录应生成总部问题行')
    assert(summary.issueRows.some((row) =>
      row.scope === 'Sunnybank (S1)' &&
      row.tableName === 'StoreRetailPrice' &&
      row.issueType === '缺失记录' &&
      row.count === 3 &&
      row.sampleProductCodes.includes('P100'),
    ), 'StoreRetailPrice 缺失记录应生成分店问题行')
    assert(summary.issueRows.some((row) =>
      row.tableName === 'StoreMultiCodeProduct' &&
      row.issueType === '孤立记录' &&
      row.sampleProductCodes.includes('P200'),
    ), 'StoreMultiCodeProduct 孤立记录应保留样本编码')
    assert(summary.issueRows.some((row) =>
      row.tableName === 'StoreMultiCodeProduct' &&
      row.issueType === '缺失记录' &&
      row.sampleProductCodes.includes('P202'),
    ), 'StoreMultiCodeProduct 缺失记录应保留样本编码')
  })
  if (productIntegritySummaryFailure) failures.push(productIntegritySummaryFailure)

  const productIntegrityAllPassFailure = await runTest('商品一致性检查无问题时 issueCount 应为 0', () => {
    const summary = buildProductIntegritySummary({
      checkTime: '2026-06-15T00:00:00Z',
      durationSeconds: 0.5,
      productSetCodeReport: {
        tableName: 'ProductSetCode',
        totalChecked: 1,
        orphanedCount: 0,
        missingCount: 0,
        invalidKeyCount: 0,
        orphanedProductCodes: [],
        missingProductCodes: [],
        errors: [],
      },
      storeReports: [],
    })

    assertEqual(summary.issueCount, 0, '无孤立和缺失记录时应视为检查通过')
    assertEqual(summary.issueRows.length, 0, '无问题时不应生成表格行')
  })
  if (productIntegrityAllPassFailure) failures.push(productIntegrityAllPassFailure)

  const productIntegrityInvalidKeyFailure = await runTest('商品一致性检查只有无效关键编码时也应显示问题', () => {
    const summary = buildProductIntegritySummary({
      checkTime: '2026-06-15T00:00:00Z',
      durationSeconds: 0.5,
      productSetCodeReport: {
        tableName: 'ProductSetCode',
        totalChecked: 1,
        orphanedCount: 0,
        missingCount: 0,
        invalidKeyCount: 3,
        orphanedProductCodes: [],
        missingProductCodes: [],
        errors: ['ProductSetCode 存在 3 条缺少 ProductCode 或 SetProductCode 的记录，未参与自动修复。'],
      },
      storeReports: [],
    })

    assertEqual(summary.issueCount, 3, '无效关键编码应计入问题数')
    assertEqual(summary.issueRows.length, 1, '无效关键编码应生成问题行')
    assertEqual(summary.issueRows[0].issueType, '无效关键编码', '问题行类型应标记为无效关键编码')
    assert(
      summary.issueRows[0].sampleProductCodes.some((message) => message.includes('缺少 ProductCode')),
      '问题行应保留后端错误说明',
    )
  })
  if (productIntegrityInvalidKeyFailure) failures.push(productIntegrityInvalidKeyFailure)

  const productIntegrityFixSummaryFailure = await runTest('商品一致性修复结果应从 reports 汇总', () => {
    const summary = buildProductIntegrityFixSummary({
      fixTime: '2026-06-15T00:00:00Z',
      durationSeconds: 2,
      isDryRun: false,
      reports: [
        {
          tableName: 'StoreRetailPrice',
          deletedCount: 2,
          addedCount: 3,
          errorCount: 0,
          errors: [],
        },
        {
          tableName: 'StoreMultiCodeProduct',
          deletedCount: 1,
          addedCount: 4,
          errorCount: 1,
          errors: ['S1 修复失败'],
        },
      ],
    })

    assertEqual(summary.deletedCount, 3, '修复结果应汇总删除数量')
    assertEqual(summary.addedCount, 7, '修复结果应汇总新增数量')
    assertEqual(summary.errorCount, 1, '修复结果应汇总错误数量')
    assert(summary.errors.includes('S1 修复失败'), '修复结果应保留错误明细')
  })
  if (productIntegrityFixSummaryFailure) failures.push(productIntegrityFixSummaryFailure)

  const productIntegritySourceFailure = await runTest('商品一致性检查页面和类型应使用后端分组报告结构', () => {
    assert(
      productIntegrityTypeSource.includes('storeReports: StoreIntegrityReport[]') &&
        productIntegrityTypeSource.includes('productSetCodeReport?: TableIntegrityReport | null') &&
        productIntegrityTypeSource.includes('reports: TableFixReport[]'),
      '商品一致性类型应声明后端真实返回的分组报告和修复 reports',
    )
    assert(
      productIntegrityHelperSource.includes('后端返回的是按分店、按表聚合的报告') &&
        productIntegrityHelperSource.includes('issueCount') &&
        productIntegrityHelperSource.includes('sampleProductCodes'),
      '商品一致性 helper 应把后端分组报告转换成页面 summary 和问题行',
    )
    assert(
      pageSource.includes('buildProductIntegritySummary') &&
        pageSource.includes('integritySummary.issueCount') &&
        pageSource.includes('sampleProductCodes'),
      '商品管理页应通过 summary 展示检查结果',
    )
    assert(
      pageSource.includes('fixStoreRetailPrice: true') &&
        pageSource.includes('fixStoreMultiCodeProduct: true') &&
        pageSource.includes('fixProductSetCode: true') &&
        pageSource.includes('buildProductIntegrityFixSummary'),
      '商品管理页自动修复应提交后端真实字段并汇总 reports',
    )
    assert(
      !pageSource.includes('integrityResult.issues') &&
        !pageSource.includes('integrityResult.totalProducts') &&
        !pageSource.includes('integrityResult.passedCount') &&
        !pageSource.includes('result.fixedCount'),
      '商品管理页不应再读取旧版扁平字段',
    )
  })
  if (productIntegritySourceFailure) failures.push(productIntegritySourceFailure)

  // HQ → HBweb 的商品全量/增量/选中同步已于 2026-09-29 停用（后端返回 410），页面不得再保留入口与轮询逻辑。
  const adminButtonGuardFailure = await runTest('页面不再提供从 HQ 拉取商品的全量、增量和选中同步入口', () => {
    for (const removed of [
      "t('posAdmin.products.fullSyncFromHQ'",
      "t('posAdmin.products.incrementalSyncFromHQ'",
      "t('posAdmin.products.syncSelectedFromHq'",
      'ensureCanSyncProductsFromHq',
      'openHqSyncModal',
      'handleSyncFromHq',
      'handleSyncSelectedFromHq',
      'syncSelectedProductsFromHq',
      'createProductFullHqSyncJob',
      'createProductIncrementalHqSyncJob',
      'getProductHqSyncJob',
      'createProductHqSyncJobPoller',
      'buildProductHqSyncOperationId',
      'PRODUCT_HQ_SYNC_ACTIVE_JOB_STORAGE_KEY',
      'posAdmin.products.activeHqSyncJob',
      'activeHqSyncJob',
      'hqSyncSubmitting',
      'hqSyncVisible',
      'selectedFromHqLoading',
      'buildHqSyncResultLines',
    ]) {
      assert(!pageSource.includes(removed), `页面不应再包含 HQ → HBweb 商品同步代码：${removed}`)
    }
    assert(
      pageSource.includes('useAuthStore') && !pageSource.includes('state.access.isAdmin'),
      '页面仍通过 auth store 读取权限，但不再需要仅供 HQ 同步使用的 isAdmin',
    )
  })
  if (adminButtonGuardFailure) failures.push(adminButtonGuardFailure)

  const writePermissionGuardFailure = await runTest('页面写操作应使用 POS 商品管理权限控制', () => {
    assert(
      pageSource.includes('canManagePosProducts'),
      '页面应读取 canManagePosProducts 控制商品编辑、分类管理、批量和同步到分店等写操作',
    )
    assert(
      pageSource.includes('ensureCanManagePosProducts') &&
        pageSource.includes("t('posAdmin.products.noManagePermission'"),
      '写操作处理函数应有统一权限保护，避免只读用户绕过按钮直接触发',
    )
  })
  if (writePermissionGuardFailure) failures.push(writePermissionGuardFailure)

  const createProductModalFailure = await runTest('页面应提供创建商品弹窗并调用 create-with-prices 接口', () => {
    assert(
      typeSource.includes('CreateProductWithPricesDto') &&
        typeSource.includes('CreateProductWithPricesResultDto') &&
        typeSource.includes('storeProductCodes: Record<string, string>'),
      '商品类型定义应声明创建商品请求和结果 DTO',
    )
    assert(
      serviceSource.includes('createProductWithPrices') &&
        serviceSource.includes("`${API_BASE}/create-with-prices`"),
      '服务层应提供 createProductWithPrices 并调用 create-with-prices 接口',
    )
    assert(
      pageSource.includes('canCreateStoreProducts') &&
        pageSource.includes("t('posAdmin.products.createProduct', '创建商品')") &&
        pageSource.includes('openCreateModal') &&
        pageSource.includes('handleCreateSave'),
      '页面应读取 canCreateStoreProducts，并提供创建商品按钮、打开弹窗和保存处理函数',
    )
    assert(
      pageSource.includes('ensureCanCreateStoreProducts') &&
        pageSource.includes("t('posAdmin.products.noCreatePermission'"),
      '创建商品入口和提交都应有单独的创建权限守卫',
    )
    assert(
      pageSource.includes('const [createVisible, setCreateVisible] = useState(false)') &&
        pageSource.includes('const [createSubmitting, setCreateSubmitting] = useState(false)') &&
        pageSource.includes('const [createForm] = Form.useForm()'),
      '页面应维护创建商品弹窗、提交状态和独立表单实例',
    )
    assert(
      pageSource.includes('await createProductWithPrices({') &&
        pageSource.includes('productType: 0') &&
        pageSource.includes('isActive: true'),
      '创建商品提交应固定普通商品，并默认启用',
    )
    assert(
      pageSource.includes('Object.keys(result.storeProductCodes ?? {}).length') &&
        pageSource.includes('result.productCode') &&
        pageSource.includes("t('posAdmin.products.createProductSuccess'"),
      '创建成功提示应展示 productCode 和已创建分店商品数量',
    )
    assert(
      pageSource.includes('setCreateVisible(false)') &&
        pageSource.includes('createForm.resetFields()') &&
        pageSource.includes('void loadData()'),
      '创建成功后应关闭弹窗、清空表单并刷新列表',
    )
    assert(
      pageSource.includes("title={t('posAdmin.products.createProduct', '创建商品')}") &&
        pageSource.includes('open={createVisible}') &&
        pageSource.includes('confirmLoading={createSubmitting}') &&
        pageSource.includes("name=\"productName\"") &&
        pageSource.includes("name=\"productImage\"") &&
        pageSource.includes("name=\"barcode\"") &&
        pageSource.includes("name=\"localSupplierCode\"") &&
        pageSource.includes("name=\"purchasePrice\"") &&
        pageSource.includes("name=\"retailPrice\""),
      '创建商品弹窗应包含基础字段并绑定提交状态',
    )
    assert(
      !pageSource.includes("t('posAdmin.products.setProduct', '套装商品')") ||
        pageSource.includes('name="productType"'),
      '创建商品弹窗范围内不应支持套装/多码切换',
    )
  })
  if (createProductModalFailure) failures.push(createProductModalFailure)

  const editSetCodesIsolationFailure = await runTest('商品编辑弹窗应隔离套装和多码明细状态', () => {
    assert(
      pageSource.includes('resetEditSetCodeState') &&
        pageSource.includes('setEditSetCodes([])') &&
        pageSource.includes('setEditSetPriceEdits({})') &&
        pageSource.includes('setEditPendingDeletes({})'),
      '页面应提供统一重置函数，清空编辑弹窗条码明细、编辑缓存和待删缓存',
    )

    const openEditStart = pageSource.indexOf('const openEdit = (record: PosProductDto) => {')
    const openEditEnd = pageSource.indexOf('const openStoreRecords', openEditStart)
    assert(openEditStart >= 0 && openEditEnd > openEditStart, '页面应保留 openEdit 编辑入口')
    const openEditSource = pageSource.slice(openEditStart, openEditEnd)
    assertSourceOrder(
      openEditSource,
      'resetEditSetCodeState()',
      'setEditingProduct(record)',
      '打开新商品编辑弹窗前应先清空上一个商品的条码明细状态',
    )

    const effectStart = pageSource.indexOf('useEffect(() => {\n    const requestSeq = editSetCodesRequestSeqRef.current + 1')
    const effectEnd = pageSource.indexOf('const handleProductTypeChange', effectStart)
    assert(effectStart >= 0 && effectEnd > effectStart, '加载编辑弹窗条码明细的 effect 应使用请求序号保护')
    const effectSource = pageSource.slice(effectStart, effectEnd)
    assert(
      pageSource.includes('const editSetCodesRequestSeqRef = useRef(0)'),
      '页面应使用 ref 保存条码明细请求序号，避免旧请求覆盖新商品',
    )
    assert(
      pageSource.includes('const editingProductCode = editingProduct?.productCode'),
      '多码/套装明细加载依赖应绑定当前编辑商品 productCode',
    )
    assert(
      effectSource.includes('loadCompleteSetCodeDraftRows({') &&
        effectSource.includes('getGridData({ productCode: editingProductCode, pageIndex, pageSize })') &&
        effectSource.includes('getRowId: (item) => item.id ?? item.setCodeId'),
      '条码明细加载应使用当前编辑商品 productCode 并校验完整快照',
    )
    assert(
      effectSource.includes('if (requestSeq === editSetCodesRequestSeqRef.current)') &&
        effectSource.includes('setEditSetCodes(items)') &&
        effectSource.includes('setEditSetCodesLoading(false)'),
      '条码明细请求返回和 loading 收尾都应先校验请求序号',
    )
    assert(
      pageSource.includes('}, [editVisible, productTypeWatch, editingProductCode, resetEditSetCodeState, t])'),
      '条码明细加载 effect 依赖应包含当前商品 productCode、类型和重置函数',
    )
    assert(
      effectSource.includes('resetEditSetCodeState()') &&
        effectSource.includes('setEditSetCodesLoading(false)'),
      '非套装/非多码状态应清空条码明细并退出 loading',
    )
  })
  if (editSetCodesIsolationFailure) failures.push(editSetCodesIsolationFailure)

  const editProductImageFailure = await runTest('商品编辑弹窗应显示并保留商品图片字段', () => {
    const openEditStart = pageSource.indexOf('const openEdit = (record: PosProductDto) => {')
    const openEditEnd = pageSource.indexOf('const openStoreRecords', openEditStart)
    assert(openEditStart >= 0 && openEditEnd > openEditStart, '页面应保留 openEdit 编辑入口')
    const openEditSource = pageSource.slice(openEditStart, openEditEnd)
    assert(
      openEditSource.includes('productImage: record.productImage || buildDefaultProductImageUrl(record.itemNumber || record.productCode)'),
      '打开编辑弹窗时应优先回填商品图片 URL，无图时按货号生成默认图片 URL',
    )
    assert(
      pageSource.includes("const DEFAULT_PRODUCT_IMAGE_BASE_URL = 'https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/YW200'") &&
        pageSource.includes('function buildDefaultProductImageUrl') &&
        pageSource.includes("`${DEFAULT_PRODUCT_IMAGE_BASE_URL}/${encodeURIComponent(normalizedItemNumber)}.jpg`"),
      '商品图片默认 URL 应使用 YW200 COS 地址并按货号生成 jpg',
    )

    const handleEditSaveStart = pageSource.indexOf('const handleEditSave = async () => {')
    const handleEditSaveEnd = pageSource.indexOf('const handleBatchEnable', handleEditSaveStart)
    assert(handleEditSaveStart >= 0 && handleEditSaveEnd > handleEditSaveStart, '页面应保留 handleEditSave 保存入口')
    const handleEditSaveSource = pageSource.slice(handleEditSaveStart, handleEditSaveEnd)
    assert(
      handleEditSaveSource.includes("productImage: values.productImage ?? editingProduct.productImage ?? ''"),
      '商品保存 payload 应显式带回 productImage，避免添加多码后覆盖清空图片字段',
    )

    const editModalStart = pageSource.indexOf('<Modal\n        open={editVisible}')
    const editModalEnd = pageSource.indexOf('{productTypeWatch === 1 &&', editModalStart)
    assert(editModalStart >= 0 && editModalEnd > editModalStart, '页面应保留编辑商品弹窗表单')
    const editModalSource = pageSource.slice(editModalStart, editModalEnd)
    assert(
      editModalSource.includes('name="productImage"') &&
        editModalSource.includes('pos-products-edit-image-preview') &&
        editModalSource.includes('prev.productImage !== cur.productImage'),
      '编辑弹窗应提供商品图片 URL 输入和随表单值变化的图片预览',
    )
  })
  if (editProductImageFailure) failures.push(editProductImageFailure)

  const editSupplierFailure = await runTest('商品编辑弹窗供应商应允许修改并随保存提交', () => {
    const handleEditSaveStart = pageSource.indexOf('const handleEditSave = async () => {')
    const handleEditSaveEnd = pageSource.indexOf('const handleBatchEnable', handleEditSaveStart)
    assert(handleEditSaveStart >= 0 && handleEditSaveEnd > handleEditSaveStart, '页面应保留 handleEditSave 保存入口')
    const handleEditSaveSource = pageSource.slice(handleEditSaveStart, handleEditSaveEnd)
    assert(
      handleEditSaveSource.includes('localSupplierCode: values.localSupplierCode'),
      '商品保存 payload 应提交编辑表单中的供应商编码',
    )

    const editModalStart = pageSource.indexOf('<Modal\n        open={editVisible}')
    const editModalEnd = pageSource.indexOf('{productTypeWatch === 1 &&', editModalStart)
    assert(editModalStart >= 0 && editModalEnd > editModalStart, '页面应保留编辑商品弹窗表单')
    const editModalSource = pageSource.slice(editModalStart, editModalEnd)
    const supplierFieldStart = editModalSource.indexOf('name="localSupplierCode"')
    const supplierFieldEnd = editModalSource.indexOf('name="productType"', supplierFieldStart)
    assert(supplierFieldStart >= 0 && supplierFieldEnd > supplierFieldStart, '编辑弹窗应保留供应商下拉表单项')
    const supplierFieldSource = editModalSource.slice(supplierFieldStart, supplierFieldEnd)
    assert(
      supplierFieldSource.includes('options={supplierOptions}') &&
        supplierFieldSource.includes("placeholder={t('posAdmin.products.selectSupplier', '请选择澳洲供应商')}") &&
        !supplierFieldSource.includes('disabled'),
      '编辑弹窗供应商下拉应打开编辑，不能禁用',
    )
  })
  if (editSupplierFailure) failures.push(editSupplierFailure)

  const productTypeColumnFailure = await runTest('商品列表应显示商品类型并按类型显示条码记录数量', () => {
    assert(
      pageSource.includes('function normalizeProductType(productType: unknown): 0 | 1 | 2') &&
        pageSource.includes('function getProductTypeColor(productType: unknown): string') &&
        pageSource.includes('const getProductTypeLabel = useCallback((productType: unknown) => {'),
      '页面应集中定义商品类型归一、颜色和文案函数',
    )
    assert(
      pageSource.includes("if (normalizedType === 1) return t('posAdmin.products.setProduct', '套装')") &&
        pageSource.includes("if (normalizedType === 2) return t('posAdmin.products.multiCodeProductShort', '多码')") &&
        pageSource.includes("return t('posAdmin.products.normalProduct', '普通')"),
      '商品类型文案应覆盖普通、套装和多码',
    )

    const productTypeColumnStart = pageSource.indexOf("title: t('posAdmin.products.productTypeLabel', '商品类型')")
    const barcodeRecordColumnStart = pageSource.indexOf("title: t('posAdmin.products.barcodeRecordCount', '条码记录')")
    const storeRecordColumnStart = pageSource.indexOf("title: t('posAdmin.products.storeRecords', '分店记录')")
    assert(productTypeColumnStart >= 0, '表格应存在商品类型列')
    assert(barcodeRecordColumnStart > productTypeColumnStart, '条码记录列应位于商品类型列之后')
    assert(storeRecordColumnStart > barcodeRecordColumnStart, '分店记录列应位于条码记录列之后')

    const productTypeColumnSource = pageSource.slice(productTypeColumnStart, barcodeRecordColumnStart)
    assert(
      productTypeColumnSource.includes("dataIndex: 'productType'") &&
        productTypeColumnSource.includes('getProductTypeColor(v)') &&
        productTypeColumnSource.includes('getProductTypeLabel(v)'),
      '商品类型列应读取 productType 并显示类型 Tag',
    )

    const barcodeRecordColumnSource = pageSource.slice(barcodeRecordColumnStart, storeRecordColumnStart)
    assert(
      barcodeRecordColumnSource.includes("dataIndex: 'setCount'") &&
        barcodeRecordColumnSource.includes('if (!isBarcodeManagedProduct(record.productType))') &&
        barcodeRecordColumnSource.includes('return <span>0</span>') &&
        barcodeRecordColumnSource.includes('openSetCodeManager(record)'),
      '条码记录列应读取 setCount，多码/套装按 productType 判断，普通商品显示 0',
    )
    assert(
      !barcodeRecordColumnSource.includes("dataIndex: 'isSet'") &&
        !barcodeRecordColumnSource.includes('record.isSet'),
      '条码记录列不能再依赖 isSet，否则多码商品数量会漏显示',
    )
  })
  if (productTypeColumnFailure) failures.push(productTypeColumnFailure)

  const productTypeActionFailure = await runTest('商品列表操作列应允许套装和多码进入条码管理', () => {
    const actionColumnStart = pageSource.indexOf("key: 'actions'")
    const columnsEnd = pageSource.indexOf('  ]', actionColumnStart)
    assert(actionColumnStart >= 0 && columnsEnd > actionColumnStart, '页面应保留操作列')
    const actionColumnSource = pageSource.slice(actionColumnStart, columnsEnd)
    assert(
      actionColumnSource.includes('isBarcodeManagedProduct(record.productType)') &&
        actionColumnSource.includes('normalizeProductType(record.productType) === 2') &&
        actionColumnSource.includes("t('posAdmin.products.multiBarcodeManagement', '多码管理')") &&
        actionColumnSource.includes("t('posAdmin.products.setManagement', '套装管理')"),
      '操作列应按 productType 允许套装和多码进入条码管理，并按类型显示按钮文案',
    )
    assert(
      !actionColumnSource.includes('record.isSet &&'),
      '操作列不能继续只按 isSet 展示条码管理入口',
    )
  })
  if (productTypeActionFailure) failures.push(productTypeActionFailure)

  const categoryParentValueFailure = await runTest('商品分类父级 Cascader 应只提交叶子 GUID', () => {
    assert(
      pageSource.includes('resolveCascaderLeafValue') &&
        pageSource.includes('const parentGuid = resolveCascaderLeafValue(values.parentGuid)'),
      '分类父级保存前应把 Cascader 路径转换为最后一级 GUID',
    )
    assert(
      pageSource.includes('parentGuid: getCategoryValueFromGuid(node.parentGuid, categoryTree)'),
      '编辑分类时应使用完整路径回填父分类 Cascader',
    )
    assert(
      pageSource.includes('categoryParentDisabledGuids.has(parentGuid)') &&
        pageSource.includes('invalidParentCategory'),
      '保存分类时应再次校验父级不能是自身或子分类，避免绕过 UI 禁用',
    )
  })
  if (categoryParentValueFailure) failures.push(categoryParentValueFailure)

  const syncFieldsRequestFailure = await runTest('同步到分店应改为后台 job 提交并轮询结果', () => {
    assert(
      typeSource.includes('SyncProductsToStoresField') &&
        typeSource.includes('fields: SyncProductsToStoresField[]'),
      'SyncProductsToStoresRequest 应声明同步字段列表',
    )
    assert(
      typeSource.includes('SyncProductsToStoresJobResult') &&
        typeSource.includes('jobId: string') &&
        typeSource.includes('status: SyncProductsToStoresJobStatus') &&
        typeSource.includes('operationId?: string') &&
        typeSource.includes('isDuplicateRequest?: boolean'),
      '类型层应声明同步到分店后台 job 结果、状态和重复提交标记',
    )
    assert(
      serviceSource.includes('startSyncProductsToStoresJob') &&
        serviceSource.includes("`${API_BASE}/sync-to-stores/jobs`") &&
        serviceSource.includes('getSyncProductsToStoresJob') &&
        serviceSource.includes("`${API_BASE}/sync-to-stores/jobs/${encodeURIComponent(jobId)}`"),
      '服务层应提供同步到分店 job 的创建与查询接口',
    )
    assert(
      pageSource.includes('buildSyncProductsToStoresFields(values)') &&
        pageSource.includes('fields: syncFields') &&
        pageSource.includes('selectSyncFields'),
      '同步到分店应根据复选框构造 fields，并校验至少选择一个字段',
    )
    assert(
      pageSource.includes('startSyncProductsToStoresJob(req)') &&
        pageSource.includes('createHqSyncJobPoller<SyncProductsToStoresJobResult>') &&
        pageSource.includes('getSyncProductsToStoresJob(jobId)'),
      '页面提交同步到分店后应创建后台 job，并使用共享轮询器查询任务状态',
    )
    assert(
      pageSource.includes('setSyncToStoreVisible(false)') &&
        pageSource.includes("t('posAdmin.products.syncToStoreJobSubmitted', '同步任务已提交，正在后台执行。完成后会自动提示结果。')"),
      '同步到分店 job 创建成功后应立即关闭弹窗并提示后台执行',
    )
    assert(
      pageSource.includes('createdCount') &&
        pageSource.includes('updatedCount') &&
        pageSource.includes('failedCount') &&
        pageSource.includes('result.errors') &&
        pageSource.includes('job.message'),
      '同步到分店最终提示应读取 job.result 的创建、更新、失败和错误明细，以及后端 message',
    )
    assert(
      pageSource.includes('error instanceof HqProductSyncPollingTimeoutError') &&
        pageSource.includes("t('posAdmin.products.syncToStoreJobTimeout', '后台仍在执行，请稍后刷新查看')"),
      '同步到分店轮询超时时应提示后台仍在执行，而不是误报同步完成',
    )
    assert(
      pageSource.includes('consecutivePollingFailures') &&
        pageSource.includes('error instanceof RequestError') &&
        pageSource.includes('error.status === 404') &&
        pageSource.includes('error.status === 401') &&
        pageSource.includes('error.status === 403') &&
        pageSource.includes('syncToStoreJobMissingTitle') &&
        pageSource.includes('syncToStoreJobAuthFailedTitle') &&
        pageSource.includes('syncToStoreJobPollingStoppedTitle'),
      '同步到分店 job 查询失败不应全部伪装成 Running，404/权限/连续失败都要停止轮询并提示用户',
    )
    assert(
      !pageSource.includes('await syncProductsToStores(req)') &&
        !pageSource.includes("t('posAdmin.products.syncToStoreComplete', '同步完成：成功 {{success}}，失败 {{failed}}'"),
      '页面不应继续直调同步接口或展示成功 0/失败 0 的旧提示',
    )
  })
  if (syncFieldsRequestFailure) failures.push(syncFieldsRequestFailure)

  const syncToStoreResultGuardFailure = await runTest('同步到分店 job 结果展示应区分失败、部分成功和成功', () => {
    const showResultStart = pageSource.indexOf('function showSyncToStoreJobResult(job: SyncProductsToStoresJobResult)')
    const showResultEnd = pageSource.indexOf('const handleEditFormValuesChange', showResultStart)
    assert(showResultStart >= 0 && showResultEnd > showResultStart, '页面应保留 showSyncToStoreJobResult 结果展示函数')
    const showResultSource = pageSource.slice(showResultStart, showResultEnd)

    assertSourceOrder(
      showResultSource,
      "if (job.status === 'Failed')",
      'if (errors.length || (result.failedCount ?? 0) > 0)',
      'job.status 为 Failed 时应先进入失败分支，不能被 failedCount/errors 误判为部分成功',
    )
    assert(
      showResultSource.includes("Modal.error({\n        title: t('posAdmin.products.syncToStoreFailed', '同步到分店失败')") &&
        showResultSource.includes("Modal.warning({\n        title: t('posAdmin.products.syncToStorePartialSucceeded', '同步到分店部分成功')") &&
        showResultSource.includes("Modal.success({\n      title: t('posAdmin.products.syncToStoreSucceeded', '同步到分店完成')"),
      '同步到分店 job 结果应分别使用失败、部分成功 warning 和成功弹窗',
    )
    assert(
      showResultSource.includes('const errors = result.errors ?? job.errors ?? []') &&
        showResultSource.includes('errors.length || (result.failedCount ?? 0) > 0'),
      'status Succeeded 但存在 failedCount/errors 时应显示部分成功 warning',
    )
    const failedBranchStart = showResultSource.indexOf("if (job.status === 'Failed')")
    const failedBranchReturn = showResultSource.indexOf('      return', failedBranchStart)
    const partialBranchStart = showResultSource.indexOf('if (errors.length || (result.failedCount ?? 0) > 0)', failedBranchStart)
    const failedBranch = showResultSource.slice(failedBranchStart, failedBranchReturn)
    assert(
      failedBranch.includes("t('posAdmin.products.syncToStoreFailed', '同步到分店失败')") &&
        !failedBranch.includes('setSelectedRowKeys([])') &&
        !failedBranch.includes('void loadData()') &&
        failedBranchReturn < partialBranchStart,
      'Failed 分支只展示失败结果并立即返回，不应清空选择或刷新成部分成功路径',
    )
  })
  if (syncToStoreResultGuardFailure) failures.push(syncToStoreResultGuardFailure)

  const syncToStorePollingGuardFailure = await runTest('同步到分店 job 轮询异常应停止并给出明确提示', () => {
    assertSourceOrder(
      pageSource,
      'if (error instanceof RequestError && (error.status === 404 || error.status === 401 || error.status === 403))',
      'consecutivePollingFailures += 1',
      '404/401/403 应立即抛出停止轮询，不能计入普通连续失败后继续伪装 Running',
    )
    assert(
      pageSource.includes('consecutivePollingFailures >= 3') &&
        pageSource.includes('throw error') &&
        pageSource.includes("title: t('posAdmin.products.syncToStoreJobPollingStoppedTitle', '同步到分店任务状态获取失败')"),
      '连续查询失败达到阈值后应停止轮询，并用 warning 告知用户刷新确认',
    )
    assert(
      pageSource.includes("title: t('posAdmin.products.syncToStoreJobMissingTitle', '同步到分店任务不存在')") &&
        pageSource.includes("title: t('posAdmin.products.syncToStoreJobAuthFailedTitle', '无法查询同步到分店任务')"),
      '同步到分店 job 轮询 404 和 401/403 应分别给出任务缺失与权限失败提示',
    )
  })
  if (syncToStorePollingGuardFailure) failures.push(syncToStorePollingGuardFailure)

  const storeRecordsFailure = await runTest('商品管理应显示分店记录数量并点击查看分店记录明细', () => {
    assert(
      typeSource.includes('storeRecordCount?: number') &&
        typeSource.includes('ProductStoreRecordDto'),
      '前端商品类型应包含分店记录数量和分店记录明细 DTO',
    )
    assert(
      serviceSource.includes('getProductStoreRecords') &&
        serviceSource.includes('/store-records'),
      '商品服务应提供按商品编码读取分店记录明细的接口',
    )
    assert(
      pageSource.includes('storeRecordCount') &&
        pageSource.includes('openStoreRecords') &&
        pageSource.includes('storeRecordsRequestSeqRef') &&
        pageSource.includes('requestSeq === storeRecordsRequestSeqRef.current') &&
        pageSource.includes('storeRecordsVisible') &&
        pageSource.includes('storeRecordsLoading') &&
        pageSource.includes('canManageStoreProducts') &&
        pageSource.includes('count > 0 && canManageStoreProducts') &&
        pageSource.includes('getProductStoreRecords(record.productCode)') &&
        pageSource.includes("dataIndex: 'storeName'") &&
        pageSource.includes('sorter: compareProductStoreRecordsByName'),
      '商品管理页面应新增分店记录数量列、点击处理、加载状态、请求竞态保护、分店名称排序器，并仅允许有分店商品权限时点击',
    )
    assert(
      pageSource.includes("t('posAdmin.products.storeRecords', '分店记录')") &&
        pageSource.includes("t('posAdmin.products.noStoreRecords', '暂无分店记录')") &&
        pageSource.includes("t('posAdmin.products.loadStoreRecordsFailed', '加载分店记录失败')"),
      '分店记录列和弹窗应有中文兜底文案',
    )
  })
  if (storeRecordsFailure) failures.push(storeRecordsFailure)

  const storeRecordFiltersFailure = await runTest('商品管理应支持分店记录数量筛选并把范围参数发送到商品列表接口', () => {
    assert(
      typeSource.includes('storeRecordCountMin?: number') &&
        typeSource.includes('storeRecordCountMax?: number'),
      'PosProductFilterParams 应声明分店记录数量最小值/最大值',
    )
    assert(
      serviceSource.includes('storeRecordCountMin: params.storeRecordCountMin') &&
        serviceSource.includes('storeRecordCountMax: params.storeRecordCountMax'),
      'getProducts 请求体应把分店记录数量范围原样发送给后端列表接口',
    )
    assert(
      pageSource.includes("const [storeRecordCountMode, setStoreRecordCountMode] = useState<'all' | 'hasRecords' | 'noRecords' | 'custom'>('all')") &&
        pageSource.includes("const [storeRecordCountModeInput, setStoreRecordCountModeInput] = useState<'all' | 'hasRecords' | 'noRecords' | 'custom'>('all')") &&
        pageSource.includes('const [storeRecordCountMin, setStoreRecordCountMin] = useState<number | undefined>(undefined)') &&
        pageSource.includes('const [storeRecordCountMax, setStoreRecordCountMax] = useState<number | undefined>(undefined)') &&
        pageSource.includes('const [storeRecordCountMinInput, setStoreRecordCountMinInput] = useState<number | undefined>(undefined)') &&
        pageSource.includes('const [storeRecordCountMaxInput, setStoreRecordCountMaxInput] = useState<number | undefined>(undefined)'),
      '页面应分别维护已生效和输入中的分店记录筛选模式与范围',
    )
    assert(
      pageSource.includes('storeRecordCountMin: storeRecordCountMin') &&
        pageSource.includes('storeRecordCountMax: storeRecordCountMax'),
      '查询生效后 loadData 应把当前分店记录筛选条件带入请求参数',
    )
    assert(
      pageSource.includes('let nextStoreRecordCountMin = storeRecordCountMinInput') &&
        pageSource.includes('let nextStoreRecordCountMax = storeRecordCountMaxInput') &&
        pageSource.includes("storeRecordCountModeInput === 'hasRecords'") &&
        pageSource.includes('nextStoreRecordCountMin = 1') &&
        pageSource.includes("storeRecordCountModeInput === 'noRecords'") &&
        pageSource.includes('nextStoreRecordCountMin = 0') &&
        pageSource.includes('nextStoreRecordCountMax = 0') &&
        pageSource.includes('let nextStoreRecordCountMode = storeRecordCountModeInput') &&
        pageSource.includes("nextStoreRecordCountMode = 'all'") &&
        pageSource.includes('setStoreRecordCountMode(nextStoreRecordCountMode)') &&
        pageSource.includes('setStoreRecordCountMin(nextStoreRecordCountMin)') &&
        pageSource.includes('setStoreRecordCountMax(nextStoreRecordCountMax)'),
      '点击查询后应按筛选模式把输入条件折算成真实查询范围，再应用到请求状态',
    )
    assert(
      pageSource.includes("storeRecordCountModeInput === 'custom'") &&
        pageSource.includes('storeRecordCountMinInput === undefined') &&
        pageSource.includes('storeRecordCountMaxInput === undefined') &&
        pageSource.includes("message.warning(t('posAdmin.products.storeRecordFilterInvalidRange', '最小数量不能大于最大数量'))") &&
        pageSource.includes('return'),
      '自定义范围两端为空应回到全部，最小值大于最大值时应提示并停止查询',
    )
    assert(
      !pageSource.includes('setStoreRecordCountMin(storeRecordCountMinInput)') &&
        !pageSource.includes('setStoreRecordCountMax(storeRecordCountMaxInput)'),
      '查询时不能把输入态范围直接写入生效态，应只写入按模式折算后的范围',
    )
    assert(
      pageSource.includes("setStoreRecordCountModeInput('all')") &&
        pageSource.includes("setStoreRecordCountMode('all')") &&
        pageSource.includes('setStoreRecordCountMinInput(undefined)') &&
        pageSource.includes('setStoreRecordCountMaxInput(undefined)') &&
        pageSource.includes('setStoreRecordCountMin(undefined)') &&
        pageSource.includes('setStoreRecordCountMax(undefined)'),
      '点击重置时应清空分店记录筛选模式与范围',
    )
    assert(
      pageSource.includes("t('posAdmin.products.storeRecordFilterPlaceholder', '分店记录')") &&
        pageSource.includes("t('posAdmin.products.storeRecordFilterAll', '全部')") &&
        pageSource.includes("t('posAdmin.products.storeRecordFilterHasRecords', '有记录')") &&
        pageSource.includes("t('posAdmin.products.storeRecordFilterNoRecords', '无记录')") &&
        pageSource.includes("t('posAdmin.products.storeRecordFilterCustom', '自定义范围')") &&
        pageSource.includes("t('posAdmin.products.storeRecordFilterMin', '最小数量')") &&
        pageSource.includes("t('posAdmin.products.storeRecordFilterMax', '最大数量')"),
      '页面应提供分店记录筛选模式与范围输入控件',
    )
  })
  if (storeRecordFiltersFailure) failures.push(storeRecordFiltersFailure)

  const storeRecordListSortFailure = await runTest('商品管理主列表分店记录列应启用服务端排序映射', () => {
    assert(
      pageSource.includes("storeRecordCount: 'storerecordcount'"),
      'SORT_FIELD_MAP 应把 storeRecordCount 映射到后端 storerecordcount 排序字段',
    )
    assert(
      pageSource.includes("dataIndex: 'storeRecordCount'") &&
        pageSource.includes('sorter: true') &&
        pageSource.includes("sortOrder: sortBy === 'storeRecordCount' ? sortOrder : undefined"),
      '分店记录主列表列应开启服务端排序，并把当前排序状态绑定到 storeRecordCount',
    )
    assert(
      pageSource.includes('count > 0 && canManageStoreProducts') &&
        pageSource.includes('compareProductStoreRecordsByName'),
      '分店记录主列表列仍应保持只有有权限且数量大于 0 时才可点击查看明细',
    )
  })
  if (storeRecordListSortFailure) failures.push(storeRecordListSortFailure)

  const productColumnFiltersFailure = await runTest('商品管理主列表应支持列头筛选并走后端查询参数', () => {
    assert(
      typeSource.includes('columnFilters?: PosProductColumnFilters') &&
        typeSource.includes("export type PosProductTextFilterOperator = 'contains' | 'equals' | 'startsWith' | 'endsWith'") &&
        typeSource.includes("export type PosProductNumberFilterOperator = 'equals' | 'between' | 'gte' | 'lte'") &&
        typeSource.includes("export type PosProductDateFilterOperator = 'equals' | 'between' | 'gte' | 'lte'"),
      '商品查询类型应声明列头筛选和文本/数字/日期操作符',
    )
    assert(
      serviceSource.includes('TEXT_FILTER_TYPE_MAP') &&
        serviceSource.includes('NUMBER_FILTER_TYPE_MAP') &&
        serviceSource.includes('applyTextColumnFilter') &&
        serviceSource.includes('applyNumberColumnFilter') &&
        serviceSource.includes('applyDateColumnFilter') &&
        serviceSource.includes('localSupplierCodes') &&
        serviceSource.includes('isAutoPricingValues') &&
        serviceSource.includes('productTypeValues') &&
        serviceSource.includes("if (operator === 'lte')") &&
        serviceSource.includes('payload[maxField] = value'),
      '商品服务应把列头筛选映射为后端 ProductReactFilterDto 字段',
    )
    assert(
      pageSource.includes('const [columnFilters, setColumnFilters] = useState<PosProductColumnFilters>({})') &&
        pageSource.includes('columnFilters,') &&
        pageSource.includes('normalizeProductTableFilters(filters as Record<string, FilterValue | null>)') &&
        pageSource.includes('setColumnFilters(nextColumnFilters)') &&
        pageSource.includes('setPage(1)') &&
        pageSource.includes('setSelectedRowKeys([])'),
      '页面应保存列头筛选状态，并在表格筛选/排序变化后回到第一页且清空选择',
    )
    assert(
      pageSource.includes('function hasProductColumnFilterValue') &&
        pageSource.includes('提交查询时再过滤空筛选') &&
        pageSource.includes("return JSON.stringify({ operator, value: value.trim() })"),
      '列头筛选下拉应保留空值 draft token，避免先选操作符时被重置',
    )
    assert(
      pageSource.includes('buildTextFilterDropdown') &&
        pageSource.includes('buildNumberFilterDropdown') &&
        pageSource.includes('buildDateFilterDropdown') &&
        pageSource.includes('renderColumnFilterPanel') &&
        pageSource.includes('pos-products-column-filter-panel') &&
        pageSource.includes("textFilterProps('productCode'") &&
        pageSource.includes("textFilterProps('itemNumber'") &&
        pageSource.includes("numberFilterProps('purchasePrice')") &&
        pageSource.includes("dateFilterProps('createdAt')") &&
        pageSource.includes("enumFilterProps('isActive'"),
      '页面应给文本、数字、日期和枚举列绑定列头筛选控件',
    )
    assert(
      globalStyleSource.includes('.pos-products-column-filter-panel') &&
        globalStyleSource.includes('.pos-products-column-filter-actions') &&
        globalStyleSource.includes('.pos-products-compact-table .ant-table-filter-column') &&
        globalStyleSource.includes('.pos-products-compact-table .ant-table-filter-column-title'),
      '商品管理列头筛选弹层和表头应有统一样式，避免控件拥挤和标题竖排',
    )
    assert(
      pageSource.includes("title: t('posAdmin.products.productCode', '商品编码')") &&
        pageSource.includes('width: 86') &&
        pageSource.includes("title: t('posAdmin.products.autoPricing', '自动定价')") &&
        pageSource.includes('width: 92') &&
        pageSource.includes("title: t('posAdmin.products.productTypeLabel', '商品类型')") &&
        pageSource.includes('width: 88') &&
        pageSource.includes('scroll={{ x: 1920, y: tableScrollY }}'),
      '接入筛选/排序后的窄列表头应放宽列宽，并同步表格横向滚动宽度',
    )
    assert(
      pageSource.includes('render: (v: string) => <BarcodeTextCell value={v} />') &&
        !pageSource.includes('className="pos-products-barcode-preview"'),
      '主表条码列应只显示条码文本、悬停时才绘制条形码，不得每行常驻一个条码 canvas',
    )
    assert(
      pageSource.includes('setColumnFilters({})') &&
        !pageSource.includes("title: t('posAdmin.products.unitWeight', '重量')"),
      '重置应清空列头筛选，且主表重量列应移除',
    )
  })
  if (productColumnFiltersFailure) failures.push(productColumnFiltersFailure)

  const productAutoPricingColumnFailure = await runTest('商品管理主列表应显示自动定价列', () => {
    const columnsStart = pageSource.indexOf('const columns: ColumnsType<ProductRow> = [')
    const columnsEnd = pageSource.indexOf('\n  ]\n\n  return (', columnsStart)
    const mainColumnsSource = pageSource.slice(columnsStart, columnsEnd)

    assert(
      mainColumnsSource.includes("title: t('posAdmin.products.autoPricing', '自动定价')") &&
        mainColumnsSource.includes("dataIndex: 'isAutoPricing'") &&
        mainColumnsSource.includes("<Tag color={value ? 'green' : 'default'}>") &&
        mainColumnsSource.includes("t('common.yes', '是')") &&
        mainColumnsSource.includes("t('common.no', '否')"),
      '商品管理主表应新增自动定价列，并以是/否展示 ProductRow.isAutoPricing',
    )
  })
  if (productAutoPricingColumnFailure) failures.push(productAutoPricingColumnFailure)

  const productBatchBooleanSwitchFailure = await runTest('商品普通批量编辑应以字段勾选配合布尔开关提交', () => {
    const openBatchEditStart = pageSource.indexOf('const openBatchEdit = () => {')
    const openBatchEditEnd = pageSource.indexOf('\n  const setImageBatchTemplate', openBatchEditStart)
    const openBatchEditSource = pageSource.slice(openBatchEditStart, openBatchEditEnd)
    const handleBatchEditStart = pageSource.indexOf('const handleBatchEditSave = async () => {')
    const handleBatchEditEnd = pageSource.indexOf('\n  const openHqSyncModal', handleBatchEditStart)
    const handleBatchEditSource = pageSource.slice(handleBatchEditStart, handleBatchEditEnd)
    const batchEditModalStart = pageSource.indexOf('<Modal\n        open={batchEditVisible}')
    const batchEditModalEnd = pageSource.indexOf('<Modal\n        open={syncToStoreVisible}', batchEditModalStart)
    const batchEditModalSource = pageSource.slice(batchEditModalStart, batchEditModalEnd)
    const autoPricingFieldStart = batchEditModalSource.indexOf("<Form.Item label={t('posAdmin.products.isAutoPricing', '自动定价')}>")
    const specialProductFieldStart = batchEditModalSource.indexOf("<Form.Item label={t('posAdmin.products.isSpecialProduct', '特殊商品')}>")
    const activeFieldStart = batchEditModalSource.indexOf('<Form.Item name="isActive"', specialProductFieldStart)
    const autoPricingFieldSource = batchEditModalSource.slice(autoPricingFieldStart, specialProductFieldStart)
    const specialProductFieldSource = batchEditModalSource.slice(specialProductFieldStart, activeFieldStart)

    assert(
      openBatchEditSource.includes('batchEditForm.setFieldsValue({') &&
        openBatchEditSource.includes('isAutoPricing: false') &&
        openBatchEditSource.includes('isSpecialProduct: false'),
      '打开普通批量编辑时应把两个布尔值明确初始化为 false',
    )
    assert(
      autoPricingFieldSource.includes('name="updateIsAutoPricing" valuePropName="checked"') &&
        autoPricingFieldSource.includes('<Checkbox>') &&
        autoPricingFieldSource.includes('name="isAutoPricing" valuePropName="checked" initialValue={false}') &&
        autoPricingFieldSource.includes('<Switch') &&
        autoPricingFieldSource.includes("checkedChildren={t('common.yes', '是')}") &&
        autoPricingFieldSource.includes("unCheckedChildren={t('common.no', '否')}") &&
        !autoPricingFieldSource.includes('<Select'),
      '普通批量编辑的自动定价应由字段勾选控制，并使用默认关闭的是/否开关',
    )
    assert(
      specialProductFieldSource.includes('name="updateIsSpecialProduct" valuePropName="checked"') &&
        specialProductFieldSource.includes('<Checkbox>') &&
        specialProductFieldSource.includes('name="isSpecialProduct" valuePropName="checked" initialValue={false}') &&
        specialProductFieldSource.includes('<Switch') &&
        specialProductFieldSource.includes("checkedChildren={t('common.yes', '是')}") &&
        specialProductFieldSource.includes("unCheckedChildren={t('common.no', '否')}") &&
        !specialProductFieldSource.includes('<Select'),
      '普通批量编辑的特殊商品应由字段勾选控制，并使用默认关闭的是/否开关',
    )
    assert(
      handleBatchEditSource.includes('isAutoPricing: values.updateIsAutoPricing ? !!values.isAutoPricing : undefined') &&
        handleBatchEditSource.includes('isSpecialProduct: values.updateIsSpecialProduct ? !!values.isSpecialProduct : undefined'),
      '普通批量编辑未勾选字段时应提交 undefined，勾选后关闭/打开应分别提交 false/true',
    )
  })
  if (productBatchBooleanSwitchFailure) failures.push(productBatchBooleanSwitchFailure)

  const storeRecordsBatchUpdateFailure = await runTest('分店记录弹窗应支持批量修改分店业务字段', () => {
    const openBatchEditStart = pageSource.indexOf('const openStoreRecordBatchEdit = () => {')
    const openBatchEditEnd = pageSource.indexOf('\n  const handleStoreRecordBatchEditSave', openBatchEditStart)
    const openBatchEditSource = pageSource.slice(openBatchEditStart, openBatchEditEnd)
    const batchEditModalStart = pageSource.indexOf('<Modal\n        open={storeRecordBatchEditVisible}')
    const batchEditModalEnd = pageSource.indexOf('<Modal\n        open={setCodeVisible}', batchEditModalStart)
    const batchEditModalSource = pageSource.slice(batchEditModalStart, batchEditModalEnd)
    const autoPricingFieldStart = batchEditModalSource.indexOf("<Form.Item label={t('posAdmin.products.autoPricing', '自动定价')}")
    const specialProductFieldStart = batchEditModalSource.indexOf("<Form.Item label={t('posAdmin.products.specialProduct', '特殊商品')}")
    const activeFieldStart = batchEditModalSource.indexOf("<Form.Item label={t('posAdmin.cashierUsers.status', '状态')}", specialProductFieldStart)
    const autoPricingFieldSource = batchEditModalSource.slice(autoPricingFieldStart, specialProductFieldStart)
    const specialProductFieldSource = batchEditModalSource.slice(specialProductFieldStart, activeFieldStart)

    assert(
      typeSource.includes('BatchUpdateProductStoreRecordsRequest') &&
        typeSource.includes('BatchUpdateProductStoreRecordsResult') &&
        typeSource.includes('purchasePrice?: number') &&
        typeSource.includes('storeRetailPriceValue?: number') &&
        typeSource.includes('discountRate?: number') &&
        typeSource.includes('isAutoPricing?: boolean') &&
        typeSource.includes('isSpecialProduct?: boolean') &&
        typeSource.includes('isActive?: boolean'),
      '类型层应声明分店记录批量修改请求/结果，以及六个可改字段',
    )
    assert(
      serviceSource.includes('batchUpdateProductStoreRecords') &&
        serviceSource.includes('/store-records/batch-update'),
      '服务层应提供分店记录批量修改接口',
    )
    assert(
      pageSource.includes('const canEditStoreProducts = useAuthStore((state) => state.access.canEditStoreProducts)'),
      '页面应从 auth store 读取 canEditStoreProducts',
    )
    assert(
      pageSource.includes('const [storeRecordSelectedRowKeys, setStoreRecordSelectedRowKeys] = useState<React.Key[]>([])') &&
        pageSource.includes('const [storeRecordBatchEditVisible, setStoreRecordBatchEditVisible] = useState(false)') &&
        pageSource.includes('const [storeRecordBatchUpdating, setStoreRecordBatchUpdating] = useState(false)') &&
        pageSource.includes('const [storeRecordBatchEditForm] = Form.useForm()'),
      '页面应维护分店记录选择、批量子弹窗可见性、提交态和表单状态',
    )
    assert(
      pageSource.includes('rowSelection={{') &&
        pageSource.includes('selectedRowKeys: storeRecordSelectedRowKeys') &&
        pageSource.includes('setStoreRecordSelectedRowKeys(keys)'),
      '分店记录表格应支持行选择，并单独维护选中 key',
    )
    assert(
      pageSource.includes("t('posAdmin.products.batchUpdateStoreRecords', '批量修改')") &&
        pageSource.includes('disabled={!canEditStoreProducts || !storeRecordSelectedRowKeys.length}') &&
        pageSource.includes("t('common.close', '关闭')"),
      '分店记录弹窗 footer 应提供受编辑权限和选中记录控制的“批量修改/关闭”按钮',
    )
    assert(
      pageSource.includes('batchUpdateProductStoreRecords(storeRecordsProduct.productCode, {') &&
        pageSource.includes('storeCodes: selectedStoreCodes') &&
        pageSource.includes('changes,'),
      '提交时应使用当前商品编码、选中分店代码和 changes 调用批量修改接口',
    )
    assert(
      pageSource.includes('const selectedStoreCodes = storeRecordSelectedRows') &&
        pageSource.includes('.map((record) => record.storeCode)') &&
        pageSource.includes('.filter((storeCode): storeCode is string => !!storeCode)'),
      '提交目标应从已选记录提取非空 storeCode',
    )
    assert(
      pageSource.includes("t('posAdmin.invoiceDetail.purchasePrice', '进货价')") &&
        pageSource.includes("t('posAdmin.invoiceDetail.retailPrice', '零售价')") &&
        pageSource.includes("t('posAdmin.productPrice.discountRate', '折扣率')") &&
        pageSource.includes("t('posAdmin.products.autoPricing', '自动定价')") &&
        pageSource.includes("t('posAdmin.products.specialProduct', '特殊商品')") &&
        pageSource.includes("t('posAdmin.cashierUsers.status', '状态')"),
      '批量修改子弹窗应包含六个业务字段',
    )
    assert(
      pageSource.includes("t('posAdmin.products.toggleFieldUpdate', '修改该字段')") &&
        pageSource.includes('precision={2}') &&
        pageSource.includes('precision={4}'),
      '每个字段应由“修改该字段”控制纳入 changes，并保留数字字段精度',
    )
    assert(
      openBatchEditSource.includes('storeRecordBatchEditForm.setFieldsValue({') &&
        openBatchEditSource.includes('isAutoPricing: false') &&
        openBatchEditSource.includes('isSpecialProduct: false'),
      '打开分店记录批量修改时应把两个布尔值明确初始化为 false',
    )
    assert(
      autoPricingFieldSource.includes('name="updateIsAutoPricing" valuePropName="checked"') &&
        autoPricingFieldSource.includes('name="isAutoPricing" valuePropName="checked" initialValue={false}') &&
        autoPricingFieldSource.includes('<Switch') &&
        autoPricingFieldSource.includes("checkedChildren={t('common.yes', '是')}") &&
        autoPricingFieldSource.includes("unCheckedChildren={t('common.no', '否')}") &&
        !autoPricingFieldSource.includes('<Select'),
      '分店记录自动定价应保留字段勾选，并使用默认关闭的是/否开关',
    )
    assert(
      specialProductFieldSource.includes('name="updateIsSpecialProduct" valuePropName="checked"') &&
        specialProductFieldSource.includes('name="isSpecialProduct" valuePropName="checked" initialValue={false}') &&
        specialProductFieldSource.includes('<Switch') &&
        specialProductFieldSource.includes("checkedChildren={t('common.yes', '是')}") &&
        specialProductFieldSource.includes("unCheckedChildren={t('common.no', '否')}") &&
        !specialProductFieldSource.includes('<Select'),
      '分店记录特殊商品应保留字段勾选，并使用默认关闭的是/否开关',
    )
    assert(
      pageSource.includes("{ enabled: !!values.updateIsAutoPricing, field: 'isAutoPricing', value: values.isAutoPricing }") &&
        pageSource.includes("{ enabled: !!values.updateIsSpecialProduct, field: 'isSpecialProduct', value: values.isSpecialProduct }"),
      '分店记录批量修改应仅在字段勾选后提交开关的 false/true 值',
    )
    assert(
      pageSource.includes('if (!storeRecordSelectedRowKeys.length) {') &&
        pageSource.includes("message.warning(t('posAdmin.products.selectStoreRecordsFirst', '请先选择分店记录'))") &&
        pageSource.includes("message.warning(t('posAdmin.products.selectAtLeastOneStoreRecordField', '请至少选择一个要修改的字段'))") &&
        pageSource.includes("message.warning(t('posAdmin.products.completeStoreRecordFields', '请填写已勾选的字段值'))"),
      '提交前应校验已选分店、至少一个字段、以及已勾选字段必须有值',
    )
    assert(
      pageSource.includes('message.success(t(\'posAdmin.products.batchUpdateStoreRecordsResult\'') &&
        pageSource.includes('success: result.successCount') &&
        pageSource.includes('failed: result.failedCount') &&
        pageSource.includes('Modal.error({') &&
        pageSource.includes('result.errors.join'),
      '提交成功后应提示成功/失败统计，有错误明细时要弹窗展示',
    )
    assert(
      pageSource.includes('await openStoreRecords(storeRecordsProduct)') &&
        pageSource.includes('await loadData()'),
      '批量修改成功后应刷新当前分店记录和主列表',
    )
    assert(
      pageSource.includes('setStoreRecordSelectedRowKeys([])') &&
        pageSource.includes('storeRecordBatchEditForm.resetFields()') &&
        pageSource.includes('setStoreRecordBatchEditVisible(false)'),
      '关闭分店记录弹窗时应清理分店记录选择和批量子弹窗状态',
    )
  })
  if (storeRecordsBatchUpdateFailure) failures.push(storeRecordsBatchUpdateFailure)

  const storeRecordSorterFailure = await runTest('分店记录名称排序应同名按分店代码兜底', () => {
    const records: ProductStoreRecordDto[] = [
      { storeCode: 'S01', storeName: 'Beta', isActive: true, isAutoPricing: false, isSpecialProduct: false },
      { storeCode: 'S99', storeName: '', isActive: true, isAutoPricing: false, isSpecialProduct: false },
      { storeCode: 'S04', storeName: 'Alpha', isActive: true, isAutoPricing: false, isSpecialProduct: false },
      { storeCode: 'S03', storeName: 'Alpha', isActive: true, isAutoPricing: false, isSpecialProduct: false },
      { storeCode: 'S02', storeName: 'Gamma', isActive: true, isAutoPricing: false, isSpecialProduct: false },
    ]

    const sortedCodes = records.slice().sort(compareProductStoreRecordsByName).map((item) => item.storeCode)

    assertEqual(sortedCodes.join(','), 'S03,S04,S01,S02,S99', '分店记录排序应先按分店名称，再按分店代码稳定排序')
  })
  if (storeRecordSorterFailure) failures.push(storeRecordSorterFailure)

  const storeRecordColumnSorterFailure = await runTest('分店记录弹窗各列应支持前端排序', () => {
    assert(
      pageSource.includes('sorter: compareProductStoreRecordsByStoreCode') &&
        pageSource.includes('sorter: compareProductStoreRecordsByName') &&
        pageSource.includes('sorter: compareProductStoreRecordsByStoreProductCode') &&
        pageSource.includes('sorter: compareProductStoreRecordsByPurchasePrice') &&
        pageSource.includes('sorter: compareProductStoreRecordsByRetailPrice') &&
        pageSource.includes('sorter: compareProductStoreRecordsByDiscountRate') &&
        pageSource.includes('sorter: compareProductStoreRecordsByAutoPricing') &&
        pageSource.includes('sorter: compareProductStoreRecordsBySpecialProduct') &&
        pageSource.includes('sorter: compareProductStoreRecordsByActive') &&
        pageSource.includes('sorter: compareProductStoreRecordsByUpdatedAt') &&
        pageSource.includes('sorter: compareProductStoreRecordsByUpdatedBy'),
      '分店记录弹窗应给分店代码、名称、编码、价格、折扣率、布尔状态、更新时间和更新人列绑定前端 sorter',
    )

    const records: ProductStoreRecordDto[] = [
      {
        storeCode: '1002',
        storeName: 'Beta',
        storeProductCode: 'B',
        purchasePrice: 4.87,
        storeRetailPriceValue: 12.5,
        discountRate: 0,
        isAutoPricing: true,
        isSpecialProduct: false,
        isActive: true,
        updatedAt: '2025-01-01T00:00:00Z',
        updatedBy: 'ReactSync',
      },
      {
        storeCode: '1001',
        storeName: 'Alpha',
        storeProductCode: 'A',
        purchasePrice: 1.25,
        storeRetailPriceValue: 9.99,
        discountRate: 0.1,
        isAutoPricing: false,
        isSpecialProduct: true,
        isActive: false,
        updatedAt: '2024-01-01T00:00:00Z',
        updatedBy: 'admin',
      },
      {
        storeCode: '1003',
        storeName: 'Gamma',
        storeProductCode: 'C',
        isAutoPricing: true,
        isSpecialProduct: false,
        isActive: true,
      },
    ]

    assertEqual(records.slice().sort(compareProductStoreRecordsByStoreCode)[0]?.storeCode, '1001', '分店代码应按文本升序排序')
    assertEqual(records.slice().sort(compareProductStoreRecordsByStoreProductCode)[0]?.storeProductCode, 'A', '分店商品编码应按文本升序排序')
    assertEqual(records.slice().sort(compareProductStoreRecordsByPurchasePrice)[0]?.purchasePrice, 1.25, '进货价应按数字升序排序')
    assertEqual(records.slice().sort(compareProductStoreRecordsByRetailPrice)[0]?.storeRetailPriceValue, 9.99, '零售价应按数字升序排序')
    assertEqual(records.slice().sort(compareProductStoreRecordsByDiscountRate)[0]?.discountRate, 0, '折扣率应按数字升序排序')
    assertEqual(records.slice().sort(compareProductStoreRecordsByAutoPricing)[0]?.isAutoPricing, false, '自动定价应按否到是排序')
    assertEqual(records.slice().sort(compareProductStoreRecordsBySpecialProduct)[0]?.isSpecialProduct, false, '特殊商品应按否到是排序')
    assertEqual(records.slice().sort(compareProductStoreRecordsByActive)[0]?.isActive, false, '状态应按禁用到启用排序')
    assertEqual(records.slice().sort(compareProductStoreRecordsByUpdatedAt)[0]?.updatedAt, '2024-01-01T00:00:00Z', '更新时间应按时间升序排序')
    assertEqual(records.slice().sort(compareProductStoreRecordsByUpdatedBy)[0]?.updatedBy, undefined, '更新人空值应按文本空值排序')
  })
  if (storeRecordColumnSorterFailure) failures.push(storeRecordColumnSorterFailure)

  const pushToHqFailure = await runTest('选中商品发送到 HQ 应复用目标分店选择、权限和防重复提交保护', () => {
    const openHandlerStart = pageSource.indexOf('const handlePushToHq = async () => {')
    const openHandlerEnd = pageSource.indexOf('const handlePushToHqConfirm', openHandlerStart)
    const openHandlerSource = pageSource.slice(openHandlerStart, openHandlerEnd)
    const confirmHandlerSource = pageSource.slice(
      pageSource.indexOf('const handlePushToHqConfirm'),
      pageSource.indexOf('const handlePushToHqCancel'),
    )
    const cancelHandlerSource = pageSource.slice(
      pageSource.indexOf('const handlePushToHqCancel'),
      pageSource.indexOf('const handleSyncSelectedFromHq'),
    )
    const loadSection = pageSource.slice(
      pageSource.indexOf('const loadPushToHqStoreOptions'),
      openHandlerStart,
    )
    const resultHandlerSource = pageSource.slice(
      pageSource.indexOf('const showPushToHqResult = useCallback'),
      pageSource.indexOf('const showSelectedFromHqResult'),
    )

    assert(
      typeSource.includes('PushProductsToHqStoreOption') &&
        typeSource.includes('targetStoreCodes?: string[]') &&
        typeSource.includes('PushProductsToHqRequest'),
      '类型层应声明目标分店选项与请求字段',
    )
    assert(
      serviceSource.includes('getPushToHqStoreOptions') &&
        serviceSource.includes("`${API_BASE}/push-to-hq/store-options`") &&
        serviceSource.includes('normalizePushToHqStoreOptions(unwrapApiData(response))'),
      '服务层应提供 HQ 分店选项接口，并对 ApiResponse.data 做前端兜底归一化',
    )
    assert(
      pageSource.includes('PosHqPushModal') &&
        pageSource.includes('pushToHqStoreOptions') &&
        pageSource.includes('getPushToHqStoreOptions'),
      '页面应复用共享发送 HQ 弹窗并维护独立 HQ 分店选项',
    )
    assert(
      pageSource.includes('const [storeOptions, setStoreOptions] = useState<StoreOption[]>([])') &&
        pageSource.includes('const [pushToHqStoreOptions, setPushToHqStoreOptions]'),
      '同步到分店本地 storeOptions 必须与 HQ 分店选项保持独立',
    )
    assert(
      loadSection.includes('await getPushToHqStoreOptions()') &&
        loadSection.includes('setPushToHqStoreOptionsError(') &&
        loadSection.includes("t('posAdmin.products.pushToHqStoreOptionsLoadFailed'") &&
        loadSection.includes('pushToHqStoreOptionsGuardRef.current') &&
        loadSection.includes('guard.begin()') &&
        loadSection.includes('requestId < 0') &&
        loadSection.includes('guard.isLatest(requestId)') &&
        loadSection.includes('guard.complete(requestId)'),
      '选项每次打开重取，并使用单飞加最新请求守卫忽略取消后的过期响应',
    )
    assert(
      openHandlerSource.includes('if (!ensureCanManagePosProducts()) return') &&
        openHandlerSource.includes('if (!selectedRowKeys.length)') &&
        openHandlerSource.includes('if (pushToHqLoadingRef.current || pushToHqModalOpen) return') &&
        openHandlerSource.includes('const productCodes = selectedRowKeys.map(String)') &&
        openHandlerSource.includes('pushToHqProductCodesRef.current = productCodes') &&
        openHandlerSource.includes('pushToHqLoadingRef.current = true') &&
        openHandlerSource.includes('setPushToHqModalOpen(true)') &&
        openHandlerSource.includes('await loadPushToHqStoreOptions()'),
      '打开弹窗应按权限与选择校验，并在即时锁内获取最新 HQ 分店选项',
    )
    assertSourceOrder(
      openHandlerSource,
      'pushToHqLoadingRef.current = true',
      'await loadPushToHqStoreOptions()',
      '选项获取必须被即时锁覆盖，防止连续打开重复请求',
    )
    assertSourceOrder(
      openHandlerSource,
      'setPushToHqModalOpen(true)',
      'await loadPushToHqStoreOptions()',
      '每次打开应先显示弹窗再拉取最新选项',
    )
    assert(
      confirmHandlerSource.indexOf('if (pushToHqLoadingRef.current || pushToHqConfirmLoadingRef.current) return') <
          confirmHandlerSource.indexOf('pushToHqConfirmLoadingRef.current = true') &&
        confirmHandlerSource.indexOf('pushToHqConfirmLoadingRef.current = true') <
          confirmHandlerSource.indexOf('await pushProductsToHq(') &&
        confirmHandlerSource.includes('pushToHqConfirmLoadingRef.current = false') &&
        confirmHandlerSource.includes('const productCodes = pushToHqProductCodesRef.current') &&
        confirmHandlerSource.includes('pushProductsToHq({') &&
        confirmHandlerSource.includes('targetStoreCodes') &&
        confirmHandlerSource.includes('if (!showPushToHqResult(result)) return') &&
        confirmHandlerSource.includes('setPushToHqModalOpen(false)') &&
        confirmHandlerSource.includes('setSelectedRowKeys([])') &&
        confirmHandlerSource.includes('await loadData()'),
      '确认提交应发送打开弹窗时快照的商品编码、updateFields 与 targetStoreCodes，并仅在成功后关闭、清选、刷新',
    )
    assert(
      resultHandlerSource.includes("if (errors.length || (result.failedCount ?? 0) > 0)") &&
        resultHandlerSource.includes('Modal.warning({') &&
        resultHandlerSource.includes('return false') &&
        resultHandlerSource.includes('Modal.success({') &&
        resultHandlerSource.includes('return true'),
      'HTTP 成功但业务部分失败时应返回 false，使弹窗保留商品、字段和分店选择',
    )
    assert(
      confirmHandlerSource.indexOf('setSelectedRowKeys([])') >
        confirmHandlerSource.indexOf('showPushToHqResult(result)'),
      '清空选择必须发生在成功后，失败时保留商品、字段和分店选择',
    )
    assert(
      confirmHandlerSource.includes('extractPushToHqErrorResult(error)') &&
        confirmHandlerSource.includes('Modal.error({'),
      '提交失败应提取后端错误明细并弹窗展示',
    )
    assert(
      confirmHandlerSource.lastIndexOf('if (isMountedRef.current)') <
          confirmHandlerSource.indexOf('setPushToHqConfirmLoading(false)') &&
        confirmHandlerSource.lastIndexOf('if (isMountedRef.current)') <
          confirmHandlerSource.indexOf('setPushToHqLoading(false)'),
      '提交 finally 释放 loading 状态前必须检查组件是否已卸载',
    )
    assert(
      cancelHandlerSource.includes('setPushToHqModalOpen(false)') &&
        cancelHandlerSource.includes('pushToHqLoadingRef.current = false') &&
        cancelHandlerSource.includes('pushToHqStoreOptionsGuardRef.current.invalidate()') &&
        cancelHandlerSource.indexOf('if (pushToHqConfirmLoadingRef.current) return') <
          cancelHandlerSource.indexOf('pushToHqLoadingRef.current = false') &&
        !cancelHandlerSource.includes('pushProductsToHq('),
      '取消只关闭弹窗、使过期选项响应失效并释放锁，且提交进行中不得释放锁',
    )
    assert(
      pageSource.includes('<PosHqPushModal') &&
        pageSource.includes('storeOptions={pushToHqStoreOptions}') &&
        pageSource.includes('onRetryStoreOptions=') &&
        pageSource.includes('onConfirm={handlePushToHqConfirm}') &&
        pageSource.includes('onCancel={handlePushToHqCancel}'),
      '页面应渲染共享弹窗并绑定选项、重试、确认与取消',
    )
    assert(
      pageSource.includes('if (!ensureCanManagePosProducts()) return') &&
        pageSource.includes('canManagePosProducts') &&
        pageSource.includes("t('posAdmin.products.pushToHq', '发送到HQ')"),
      '发送到 HQ 按钮和处理函数应复用 POS 商品管理权限',
    )
    assert(
      pageSource.includes('const pushToHqLoadingRef = useRef(false)') &&
        pageSource.includes('disabled={!selectedRowKeys.length || pushToHqLoading || pushToHqModalOpen}'),
      '发送到 HQ 应使用 ref 锁和 loading/modal 状态防止重复提交',
    )
  })
  if (pushToHqFailure) failures.push(pushToHqFailure)

  const selectedFromHqFailure = await runTest('选中商品从 HQ 同步的服务层契约保留但页面不再调用', () => {
    assert(
      typeSource.includes('SyncSelectedProductsFromHqRequest') &&
        typeSource.includes('productCodes: string[]'),
      '类型层仍声明选中商品从 HQ 同步的请求契约（服务层暂时保留，以后统一清理）',
    )
    assert(
      serviceSource.includes('syncSelectedProductsFromHq') &&
        serviceSource.includes("`${API_BASE}/sync-selected-from-hq`") &&
        serviceSource.includes('normalizeHqProductSyncResult'),
      '服务层函数暂时保留，接口已停用并返回 410',
    )
    assert(
      !pageSource.includes('syncSelectedProductsFromHq') && !pageSource.includes('showSelectedFromHqResult'),
      '页面不应再调用选中商品从 HQ 同步接口',
    )
  })
  if (selectedFromHqFailure) failures.push(selectedFromHqFailure)

  const batchTranslateFailure = await runTest('选中商品批量翻译应默认中文到英文并覆盖商品名称', () => {
    assert(
      pageSource.includes("../../../services/translationService") &&
        pageSource.includes('batchTranslate'),
      '页面应引入批量翻译服务',
    )
    assert(
      pageSource.includes('const [translating, setTranslating] = useState(false)') &&
        pageSource.includes('setTranslating(true)') &&
        pageSource.includes('setTranslating(false)'),
      '页面应维护批量翻译 loading 状态',
    )
    assert(
      pageSource.includes('containsChineseText') &&
        pageSource.includes('buildProductNameTranslationUpdates') &&
        pageSource.includes('!containsChineseText(translatedName)'),
      '批量翻译应过滤空结果、未变化结果和仍包含中文的结果',
    )
    assert(
      pageSource.includes('const selectedRows = data.filter((row) => selectedRowKeys.includes(row.key))') &&
        pageSource.includes('Array.from(new Set(selectedRows.map((row) => row.productName.trim())') &&
        pageSource.includes('const translations = await batchTranslate(names)'),
      '批量翻译应只使用当前页选中商品名称去重后调用翻译接口',
    )
    assert(
      pageSource.includes('const result = await batchUpdateProducts(updates)') &&
        pageSource.includes('productCode: row.productCode') &&
        pageSource.includes('productName: translatedName') &&
        pageSource.includes('englishName: translatedName'),
      '批量翻译应通过批量更新接口提交 productCode、翻译后的 productName 和 englishName',
    )
    assert(
      typeSource.includes('englishName?: string'),
      '批量更新商品 DTO 应声明 englishName 字段以同步写入后端 EnglishName',
    )
    assert(
      pageSource.includes("t('posAdmin.products.batchTranslate', '批量翻译')") &&
        pageSource.includes('disabled={!selectedRowKeys.length || translating}') &&
        pageSource.includes('loading={translating}'),
      '工具栏应提供受选中状态和 loading 控制的批量翻译按钮',
    )
  })
  if (batchTranslateFailure) failures.push(batchTranslateFailure)

  const jobEndpointFailure = await runTest('商品 HQ 同步 job 的服务层保留但页面不再创建 job', () => {
    assert(
      serviceSource.includes("`${SYNC_API_BASE}/products/jobs`") &&
        serviceSource.includes("`${SYNC_API_BASE}/products-incremental/jobs`") &&
        (serviceSource.includes("`${SYNC_API_BASE}/products/jobs/${encodeURIComponent(jobId)}`") ||
          serviceSource.includes("`${SYNC_API_BASE}/products/jobs/${jobId}`")),
      '服务层商品 HQ 同步 job 函数暂时保留（以后统一清理）',
    )
    assert(
      !pageSource.includes('createProductFullHqSyncJob(') &&
        !pageSource.includes('createProductIncrementalHqSyncJob(') &&
        !pageSource.includes('await syncProductsFromHqFull()') &&
        !pageSource.includes('await syncProductsFromHqIncremental({'),
      '页面不应再发起任何商品 HQ → HBweb 同步请求',
    )
  })
  if (jobEndpointFailure) failures.push(jobEndpointFailure)

  const syncResultMappingFailure = await runTest('HqProductSyncResult 类型保留新字段且页面不再展示 HQ 同步结果', () => {
    assert(
      typeSource.includes('productsAdded?: number') &&
        typeSource.includes('productsUpdated?: number') &&
        typeSource.includes('productsDeleted?: number'),
      'HqProductSyncResult 类型应声明 productsAdded/productsUpdated/productsDeleted',
    )
    // productsAdded 仍被「发送到 HQ」的结果展示使用，这里只校验 HQ → HBweb 同步结果展示已移除。
    assert(
      !pageSource.includes('showHqSyncJobResult') &&
        !pageSource.includes("t('posAdmin.products.hqSyncResult'"),
      '页面不应再展示商品 HQ 同步结果',
    )
  })
  if (syncResultMappingFailure) failures.push(syncResultMappingFailure)

  const duplicateClickGuardFailure = await runTest('「同步」菜单只保留同步到分店，不再包含 HQ 增量/全量同步', () => {
    const syncMenuStart = pageSource.indexOf("label={t('common.listToolbar.sync', '同步')}")
    const syncMenuSource = pageSource.slice(syncMenuStart, pageSource.indexOf("label={t('common.listToolbar.tools', '工具')}", syncMenuStart))
    assert(
      syncMenuStart >= 0 &&
        syncMenuSource.includes("key: 'syncToStore'") &&
        !syncMenuSource.includes("key: 'incrementalHqSync'") &&
        !syncMenuSource.includes("key: 'fullHqSync'") &&
        !syncMenuSource.includes('hqSyncSubmitting'),
      '同步菜单只应保留 HBweb 内部的同步到分店',
    )
    assert(
      !pageSource.includes('<DatePicker style={{ width: \'100%\' }} allowClear={false} format="YYYY-MM-DD" />'),
      '增量同步起始日期弹窗应随 HQ 同步一起移除',
    )
  })
  if (duplicateClickGuardFailure) failures.push(duplicateClickGuardFailure)

  const hqSyncArchitectureFailure = await runTest('商品 HQ 同步共享轮询器与 operationId 仍由服务层导出', () => {
    assert(
      serviceSource.includes('export function buildProductHqSyncOperationId') &&
        serviceSource.includes('createProductHqSyncJobPoller'),
      '服务层导出暂时保留，页面不再使用',
    )
    assert(
      !pageSource.includes("type HqSyncJobStatus = 'Queued'") &&
        !pageSource.includes('type ProductHqSyncJobResult = HqProductSyncResult &'),
      '页面不应镜像服务层已有 HQ 同步 job 类型',
    )
    assert(
      !pageSource.includes('setTimeout(poll, PRODUCT_HQ_SYNC_POLL_INTERVAL_MS)') &&
        !pageSource.includes('async function poll()'),
      '页面不应保留原始 setTimeout 轮询状态机',
    )
  })
  if (hqSyncArchitectureFailure) failures.push(hqSyncArchitectureFailure)

  const supplierImagePollingFailure = await runTest('供应商图片批量修改 job 轮询不应把 404 和权限错误伪装成 Running', () => {
    assert(
      pageSource.includes('error instanceof RequestError') &&
        pageSource.includes('error.status === 404') &&
        pageSource.includes('error.status === 401') &&
        pageSource.includes('error.status === 403'),
      '图片批量修改 job 轮询应按 RequestError.status 分类处理',
    )
    assert(
      pageSource.includes('clearActiveImageBatchJob(job.localSupplierCode)') &&
        pageSource.includes('batchImageJobMissingTitle') &&
        pageSource.includes('batchImageJobAuthFailedTitle'),
      '404/权限错误应清理 active job 并停止轮询提示用户',
    )
  })
  if (supplierImagePollingFailure) failures.push(supplierImagePollingFailure)

  const supplierImageResultRefreshFailure = await runTest('供应商图片批量修改成功后应刷新供应商与商品列表', () => {
    const resultSource = pageSource.slice(
      pageSource.indexOf('const showSupplierImageBatchResult = useCallback'),
      pageSource.indexOf('const startSyncToStoreJobPolling'),
    )
    const successSource = resultSource.slice(resultSource.indexOf('Modal.success({'))
    assert(
      successSource.includes('void refreshSupplierOptions()') &&
        successSource.includes('void loadData()') &&
        !successSource.includes('return true'),
      '完整成功分支不得在刷新供应商选项和商品列表前提前返回',
    )
  })
  if (supplierImageResultRefreshFailure) failures.push(supplierImageResultRefreshFailure)

  const supplierImageConcurrentJobFailure = await runTest('供应商图片批量修改应按供应商跟踪 active job', () => {
    assert(
      pageSource.includes('type ActiveSupplierImageBatchJobMap') &&
        pageSource.includes('readActiveSupplierImageBatchJobs') &&
        pageSource.includes('saveActiveSupplierImageBatchJobs') &&
        pageSource.includes('activeImageBatchJobs'),
      '图片批量修改 active job 应保存为按供应商代码索引的 map',
    )
    assert(
      pageSource.includes('stopSupplierImageBatchPolling(job.localSupplierCode)') &&
        pageSource.includes('stopSupplierImageBatchPollingRef.current[jobKey] = poller.stop') &&
        pageSource.includes('clearActiveImageBatchJob(job.localSupplierCode)'),
      '轮询启动、停止和清理应只作用于对应供应商',
    )
    assert(
      pageSource.includes('getActiveImageBatchJobBySupplier(values.localSupplierCode)') &&
        pageSource.includes('showActiveSupplierImageBatchStatus(existingActiveJob)') &&
        !pageSource.includes('const storedActiveJob = activeImageBatchJob ?? readActiveSupplierImageBatchJob()'),
      '提交时只阻止同一供应商已有任务，不应因为其他供应商任务而阻止打开弹窗',
    )
    assert(
      pageSource.indexOf('imageBatchForm.resetFields()') < pageSource.indexOf('getActiveImageBatchJobBySupplier(values.localSupplierCode)'),
      '打开图片批量修改弹窗时应允许切换到其他供应商，不能先用默认供应商 active job 直接拦截',
    )
    assert(
      pageSource.includes('hbwebSkippedExistingImageCount') &&
        pageSource.includes('hqSkippedExistingImageCount'),
      '结果弹窗应展示 Hbweb/HQ 已有图片跳过数量',
    )
  })
  if (supplierImageConcurrentJobFailure) failures.push(supplierImageConcurrentJobFailure)

  const keepAliveVirtualTableRestoreFailure = await runTest('KeepAlive 恢复虚拟商品表格时应重挂载并恢复滚动位置', () => {
    assert(
      pageSource.includes("import type { ColumnsType, TableRef } from 'antd/es/table'") &&
        pageSource.includes("import { useKeepAliveContext } from 'keepalive-for-react'") &&
        pageSource.includes('const { active } = useKeepAliveContext()'),
      '商品页面应读取 KeepAlive active 状态并持有 AntD TableRef',
    )
    assert(
      pageSource.includes('const [productTableRenderKey, setProductTableRenderKey] = useState(0)') &&
        pageSource.includes('const productTableRef = useRef<TableRef | null>(null)') &&
        pageSource.includes('const lastProductTableScrollTopRef = useRef(0)') &&
        pageSource.includes('const productTableHasDataRef = useRef(data.length > 0)') &&
        pageSource.includes('productTableHasDataRef.current = data.length > 0') &&
        pageSource.includes('const wasProductManagementTabActiveRef = useRef(active)') &&
        pageSource.includes('const productTableRestoreFrameRef = useRef<number | null>(null)'),
      '商品页面应同步保存虚拟表格数据状态、重挂载 key、滚动位置和待取消的动画帧',
    )
    const restoreEffectStart = pageSource.indexOf('useEffect(() => {\n    const wasActive = wasProductManagementTabActiveRef.current')
    const restoreEffectSource = pageSource.slice(restoreEffectStart, pageSource.indexOf("  useEffect(() => {\n    return () => {\n      isMountedRef.current = false", restoreEffectStart))
    assert(
      restoreEffectSource.includes('if (!active || wasActive || !productTableHasDataRef.current)') &&
        restoreEffectSource.includes('}, [active])') &&
        !restoreEffectSource.includes('data.length') &&
        restoreEffectSource.includes('window.cancelAnimationFrame(productTableRestoreFrameRef.current)'),
      '恢复 effect 只能依赖 active，并通过数据 ref 判断是否需要调度或取消动画帧',
    )
    assertSourceOrder(
      restoreEffectSource,
      'const renderFrame = window.requestAnimationFrame',
      'setProductTableRenderKey((value) => value + 1)',
      '恢复首帧应先调度并重挂载表格',
    )
    assertSourceOrder(
      restoreEffectSource,
      'setProductTableRenderKey((value) => value + 1)',
      'productTableRestoreFrameRef.current = window.requestAnimationFrame',
      '滚动位置应在重挂载后的下一帧恢复',
    )
    assert(
      !restoreEffectSource.includes('const scrollTop = lastProductTableScrollTopRef.current') &&
        restoreEffectSource.includes('productTableRef.current?.scrollTo?.({ top: lastProductTableScrollTopRef.current })'),
      '第二帧必须读取最新滚动位置，避免查询或分页归零后恢复旧位置',
    )
    assert(
      pageSource.includes('const handleProductTableScroll = (event: UIEvent<HTMLDivElement>) => {\n    if (!active) return') &&
        pageSource.includes('lastProductTableScrollTopRef.current = event.currentTarget.scrollTop'),
      '隐藏态滚动事件不得覆盖已保存的有效滚动位置',
    )
    const mainTableClassIndex = pageSource.indexOf('className="pos-products-compact-table"')
    const mainTableSource = pageSource.slice(
      pageSource.lastIndexOf(
        '<MeasuredTable metricId="pos-admin.product-management.table-1"',
        mainTableClassIndex,
      ),
      pageSource.indexOf('\n        </div>\n\n        <div\n          ref={pagerRef}', mainTableClassIndex),
    )
    assert(
      mainTableSource.includes('key={productTableRenderKey}') &&
        mainTableSource.includes('ref={productTableRef}') &&
        mainTableSource.includes('onScroll={handleProductTableScroll}'),
      '主商品虚拟表格应接入重挂载 key、ref 和滚动监听',
    )
    assert(
      mainTableSource.includes('onChange={(_pagination, filters, sorter) => {') &&
        mainTableSource.includes('resetProductTableScroll()'),
      '主表排序筛选变更时应归零滚动位置',
    )
    const searchSource = pageSource.slice(pageSource.indexOf('const handleSearch = () => {'), pageSource.indexOf('const handleReset = () => {'))
    const resetSource = pageSource.slice(pageSource.indexOf('const handleReset = () => {'), pageSource.indexOf('const buildCategoryCascaderOptions = '))
    const paginationStart = pageSource.indexOf('<Pagination\n            current={page}')
    const paginationSource = pageSource.slice(paginationStart, pageSource.indexOf('/>', paginationStart) + 2)
    assert(
      pageSource.includes('const resetProductTableScroll = () => {') &&
        searchSource.includes('resetProductTableScroll()') &&
        resetSource.includes('resetProductTableScroll()') &&
        paginationSource.includes('onChange={(p, ps) => {\n              resetProductTableScroll()'),
      '查询、重置和分页变化时应分别同步归零表格滚动位置',
    )
  })
  if (keepAliveVirtualTableRestoreFailure) failures.push(keepAliveVirtualTableRestoreFailure)

  const supplierCategoryColumnsFailure = await runTest('商品管理应加载商品/仓库分类树与国内供应商并扩展供应商与分类列', () => {
    assert(
      pageSource.includes("import { getAllChinaSuppliers } from '../../../services/chinaSupplierService'") &&
        pageSource.includes('getCategoryTree as getWarehouseCategoryTree') &&
        pageSource.includes('type WarehouseCategoryNode'),
      '页面应加载国内供应商和仓库分类服务',
    )
    assert(
      pageSource.includes('warehouseCategoryGuid?: string') &&
        pageSource.includes('domesticSupplierCode?: string') &&
        pageSource.includes('domesticSupplierName?: string'),
      '商品行类型应声明仓库分类和国内供应商响应字段',
    )
    assert(
      pageSource.includes('const [warehouseCategoryTree, setWarehouseCategoryTree] = useState<WarehouseCategoryNode[]>([])') &&
        pageSource.includes('const [warehouseCategoryLoadFailed, setWarehouseCategoryLoadFailed] = useState(false)') &&
        pageSource.includes('const [chinaSupplierOptions, setChinaSupplierOptions] = useState<ChinaSupplierOption[]>([])') &&
        pageSource.includes('const [warehouseCategoryGuid, setWarehouseCategoryGuid] = useState<string | undefined>(undefined)') &&
        pageSource.includes('const [warehouseCategoryGuidInput, setWarehouseCategoryGuidInput] = useState<string | undefined>(undefined)'),
      '页面应保存仓库分类树、失败状态、国内供应商选项和仓库分类顶部筛选状态',
    )
    const loadOptionsStart = pageSource.indexOf('const loadOptions = useCallback')
    const loadOptionsEnd = pageSource.indexOf('useEffect(() => {\n    loadOptions()', loadOptionsStart)
    const loadOptionsSource = pageSource.slice(loadOptionsStart, loadOptionsEnd)
    assert(
      loadOptionsSource.includes('const tree = await getProductCategoryTree()') &&
        loadOptionsSource.includes('setCategoryLoadFailed') &&
        loadOptionsSource.includes('const tree = await getWarehouseCategoryTree()') &&
        loadOptionsSource.includes('setWarehouseCategoryLoadFailed') &&
        loadOptionsSource.includes('getAllChinaSuppliers()'),
      'loadOptions 应独立加载商品分类树、仓库分类树和国内供应商选项',
    )
    assert(
      loadOptionsSource.includes('await Promise.all([') &&
        !loadOptionsSource.includes('await refreshSupplierOptions()'),
      'loadOptions 的五类下拉选项互不依赖，应并行加载而不是逐个 await 串行',
    )
    const loadDataStart = pageSource.indexOf('const loadData = useCallback')
    const loadDataSource = pageSource.slice(loadDataStart, pageSource.indexOf('const stopHqSyncJobPolling', loadDataStart))
    assert(
      loadDataSource.includes('productListAbortRef.current?.abort()') &&
        loadDataSource.includes('runLatestGuardedRequest(') &&
        loadDataSource.includes('getProducts(params, { signal: controller.signal })') &&
        pageSource.includes('productListRequestGuardRef.current.invalidate()'),
      '商品列表请求应取消上一个在途请求，并只允许最后一次请求写入列表，卸载时让守卫失效',
    )
    assert(
      pageSource.includes('warehouseCategoryGuid: warehouseCategoryGuid || undefined') &&
        pageSource.includes('setWarehouseCategoryGuid(warehouseCategoryGuidInput)') &&
        pageSource.includes('setWarehouseCategoryGuidInput(undefined)') &&
        pageSource.includes('setWarehouseCategoryGuid(undefined)'),
      '查询参数、查询和重置应同步仓库分类顶部筛选',
    )
    assert(
      pageSource.includes("domesticSupplierCode: 'domesticsuppliercode'") &&
        pageSource.includes("warehouseCategoryGuid: 'warehousecategoryguid'"),
      '国内供应商和仓库分类排序字段应映射到后端契约',
    )
    assert(
      pageSource.match(/text: fullPath\.join\(' \/ '\)/g)?.length === 2,
      '商品分类和仓库分类列头筛选应显示完整分类路径，避免同名叶级分类混淆',
    )
    const toolbarStart = pageSource.indexOf('<div className="list-toolbar-filter-row">')
    const toolbarEnd = pageSource.indexOf('onClick={handleSearch}', toolbarStart)
    assert(toolbarStart >= 0 && toolbarEnd > toolbarStart, '顶部筛选应使用共用筛选行，并以查询按钮收尾')
    const toolbarSource = pageSource.slice(toolbarStart, toolbarEnd)
    assertSourceOrder(
      toolbarSource,
      'onPressEnter={handleSearch}',
      'options={supplierOptions}',
      '顶部筛选应先搜索再澳洲供应商',
    )
    assertSourceOrder(
      toolbarSource,
      'options={supplierOptions}',
      'options={buildCategoryCascaderOptions(categoryTree)}',
      '顶部筛选澳洲供应商应排在商品分类前',
    )
    assertSourceOrder(
      toolbarSource,
      'options={buildCategoryCascaderOptions(categoryTree)}',
      'options={buildWarehouseCategoryCascaderOptions(warehouseCategoryTree)}',
      '顶部筛选商品分类应排在仓库分类前',
    )
    // 改版后仓库分类、套装、分店记录收进「更多筛选」，主筛选行保留搜索、澳洲供应商、商品分类、状态。
    assertSourceOrder(
      toolbarSource,
      'value={isActiveFilterInput}',
      '<MoreFiltersButton activeCount={moreFiltersActiveCount}>',
      '顶部筛选状态应排在「更多筛选」前',
    )
    const moreFiltersSource = toolbarSource.slice(
      toolbarSource.indexOf('<MoreFiltersButton activeCount={moreFiltersActiveCount}>'),
      toolbarSource.indexOf('</MoreFiltersButton>'),
    )
    assertSourceOrder(
      moreFiltersSource,
      'options={buildWarehouseCategoryCascaderOptions(warehouseCategoryTree)}',
      'value={isSetFilterInput}',
      '更多筛选内仓库分类应排在套装前',
    )
    assertSourceOrder(
      moreFiltersSource,
      'value={isSetFilterInput}',
      'value={storeRecordCountModeInput}',
      '更多筛选内套装应排在分店记录前',
    )
    assert(
      moreFiltersSource.includes('value={storeRecordCountMinInput}') &&
        moreFiltersSource.includes('value={storeRecordCountMaxInput}'),
      '分店记录自定义范围输入应放在「更多筛选」内',
    )
    assert(
      !toolbarSource.includes('domesticSupplierColumnFilterOptions') &&
        !toolbarSource.includes("t('posAdmin.products.domesticSupplier'"),
      '国内供应商不应设置顶部筛选',
    )
    const columnsStart = pageSource.indexOf('const columns: ColumnsType<ProductRow> = [')
    const columnsEnd = pageSource.indexOf('\n  ]\n\n  return (', columnsStart)
    const mainColumnsSource = pageSource.slice(columnsStart, columnsEnd)
    assertSourceOrder(
      mainColumnsSource,
      "key: 'localSupplierCode'",
      "key: 'domesticSupplierCode'",
      '表格澳洲供应商列应排在国内供应商列前',
    )
    assertSourceOrder(
      mainColumnsSource,
      "key: 'domesticSupplierCode'",
      "key: 'categoryGuid'",
      '表格国内供应商列应排在商品分类列前',
    )
    assertSourceOrder(
      mainColumnsSource,
      "key: 'categoryGuid'",
      "key: 'warehouseCategoryGuid'",
      '表格商品分类列应排在仓库分类列前',
    )
    assert(
      mainColumnsSource.includes("...enumFilterProps('domesticSupplierCode', domesticSupplierColumnFilterOptions)") &&
        mainColumnsSource.includes("...enumFilterProps('warehouseCategoryGuid', warehouseCategoryColumnFilterOptions)") &&
        mainColumnsSource.includes("title: t('posAdmin.products.australianSupplier', '澳洲供应商')") &&
        mainColumnsSource.includes("title: t('posAdmin.products.domesticSupplier', '国内供应商')") &&
        mainColumnsSource.includes("title: t('posAdmin.products.productCategoryLabel', '商品分类')") &&
        mainColumnsSource.includes("title: t('posAdmin.products.warehouseCategory', '仓库分类')"),
      '供应商与分类列应使用明确标题并绑定多选列头过滤',
    )
    assert(
      mainColumnsSource.includes("sortOrder: sortBy === 'domesticSupplierCode' ? sortOrder : undefined") &&
        mainColumnsSource.includes("sortOrder: sortBy === 'warehouseCategoryGuid' ? sortOrder : undefined"),
      '国内供应商和仓库分类列应启用服务端排序',
    )
    assert(
      pageSource.includes('function renderCategoryCell') &&
        pageSource.includes('nameByGuid.get(guid)') &&
        pageSource.includes('const displayName = name ?? guid') &&
        pageSource.includes('return <span>-</span>') &&
        pageSource.includes("path.join(' / ')") &&
        pageSource.includes("textOverflow: 'ellipsis'") &&
        pageSource.includes("whiteSpace: 'nowrap'") &&
        pageSource.includes('renderCategoryCell(record.categoryGuid, categoryPathMaps.nameByGuid, categoryPathMaps.pathByGuid)') &&
        pageSource.includes('renderCategoryCell(record.warehouseCategoryGuid, warehouseCategoryPathMaps.nameByGuid, warehouseCategoryPathMaps.pathByGuid)'),
      '分类单元格应显示叶级名、完整路径 tooltip，未知 GUID 显示 GUID，空值显示占位符',
    )
    assert(
      pageSource.includes('warehouseCategoryGuid: editingProduct.warehouseCategoryGuid'),
      '编辑商品保存时应显式带回只读仓库分类，避免覆盖式 PUT 清空分类',
    )
    assert(
      pageSource.includes("t('posAdmin.products.categoryManagement', '商品分类管理')") &&
        pageSource.includes("t('posAdmin.products.newCategory', '新建商品分类')") &&
        pageSource.includes("t('posAdmin.products.editCategory', '编辑商品分类')") &&
        pageSource.includes("t('posAdmin.products.parentCategory', '父商品分类')") &&
        pageSource.includes("t('posAdmin.products.categoryName', '商品分类名称')"),
      '分类管理入口与新建/编辑表单应明确为商品分类',
    )
    assert(
      pageSource.includes('isActive: editingCategory.isActive') &&
        pageSource.includes("placeholder={t('posAdmin.products.categoryPlaceholder', '商品分类')}"),
      '编辑商品分类应显式保留启用状态，顶部筛选应明确标注商品分类',
    )
    assert(
      pageSource.includes('scroll={{ x: 1920, y: tableScrollY }}'),
      '新增两列后表格横向滚动宽度应同步放宽',
    )
    assert(
      zhLocaleSource.includes('"australianSupplier": "澳洲供应商"') &&
        zhLocaleSource.includes('"domesticSupplier": "国内供应商"') &&
        zhLocaleSource.includes('"warehouseCategory": "仓库分类"') &&
        zhLocaleSource.includes('"warehouseCategoryPlaceholder": "仓库分类"') &&
        zhLocaleSource.includes('"categoryManagement": "商品分类管理"') &&
        zhLocaleSource.includes('"newCategory": "新建商品分类"') &&
        zhLocaleSource.includes('"editCategory": "编辑商品分类"') &&
        zhLocaleSource.includes('"parentCategory": "父商品分类"') &&
        zhLocaleSource.includes('"categoryName": "商品分类名称"') &&
        zhLocaleSource.includes('"categoryPlaceholder": "商品分类"') &&
        zhLocaleSource.includes('"supplierPlaceholder": "澳洲供应商"'),
      '中文文案应补齐国内供应商、仓库分类与商品分类管理命名',
    )
    assert(
      enLocaleSource.includes('"australianSupplier": "Australian Supplier"') &&
        enLocaleSource.includes('"domesticSupplier": "Domestic Supplier"') &&
        enLocaleSource.includes('"warehouseCategory": "Warehouse Category"') &&
        enLocaleSource.includes('"warehouseCategoryPlaceholder": "Warehouse Category"') &&
        enLocaleSource.includes('"categoryManagement": "Product Category Management"') &&
        enLocaleSource.includes('"newCategory": "New Product Category"') &&
        enLocaleSource.includes('"editCategory": "Edit Product Category"') &&
        enLocaleSource.includes('"parentCategory": "Parent Product Category"') &&
        enLocaleSource.includes('"categoryName": "Product Category Name"') &&
        enLocaleSource.includes('"categoryPlaceholder": "Product Category"') &&
        enLocaleSource.includes('"supplierPlaceholder": "Australian Supplier"'),
      '英文文案应与中文同步补齐',
    )
    const zhProductMessages = (JSON.parse(zhLocaleSource) as {
      posAdmin: { products: Record<string, string> }
    }).posAdmin.products
    const enProductMessages = (JSON.parse(enLocaleSource) as {
      posAdmin: { products: Record<string, string> }
    }).posAdmin.products
    assertEqual(zhProductMessages.supplier, '澳洲供应商', '中文商品页不应继续使用泛称供应商')
    assertEqual(zhProductMessages.selectSupplier, '请选择澳洲供应商', '中文商品页供应商选择提示应明确为澳洲供应商')
    assertEqual(zhProductMessages.searchPlaceholder, '搜索商品名称 / 货号 / 条码 / 英文名 / 澳洲供应商', '中文搜索提示应明确供应商字段为澳洲供应商')
    assertEqual(enProductMessages.supplier, 'Australian Supplier', '英文商品页不应继续使用泛称 Supplier')
    assertEqual(enProductMessages.selectSupplier, 'Select Australian Supplier', '英文商品页供应商选择提示应明确为 Australian Supplier')
    assertEqual(enProductMessages.searchPlaceholder, 'Search product name / item no. / barcode / English name / Australian supplier', '英文搜索提示应明确 supplier 字段为 Australian supplier')
  })
  if (supplierCategoryColumnsFailure) failures.push(supplierCategoryColumnsFailure)

  const toolbarLayoutFailure = await runTest('商品管理页头应按菜单分组，批量操作只出现在勾选后操作条', () => {
    const headerStart = pageSource.indexOf('<PageContainer\n      compact')
    const headerEnd = pageSource.indexOf('<div\n        ref={wrapRef}', headerStart)
    const headerSource = pageSource.slice(headerStart, headerEnd)
    assert(headerStart >= 0 && headerEnd > headerStart, '商品管理应使用紧凑页头 PageContainer compact')
    assert(
      headerSource.includes("subtitle={t('posAdmin.products.subtitle', { count: total })}"),
      '紧凑页头应保留「共 N 条记录」副标题',
    )
    const syncMenuSource = headerSource.slice(
      headerSource.indexOf("label={t('common.listToolbar.sync', '同步')}"),
      headerSource.indexOf("label={t('common.listToolbar.tools', '工具')}"),
    )
    // HQ → HBweb 的增量/全量同步已于 2026-09-29 停用，「同步」菜单只保留同步到分店。
    assert(
      !syncMenuSource.includes("key: 'incrementalHqSync'") &&
        !syncMenuSource.includes("key: 'fullHqSync'") &&
        !syncMenuSource.includes('openHqSyncModal') &&
        syncMenuSource.includes("key: 'syncToStore'") &&
        syncMenuSource.includes('visible: canManagePosProducts,') &&
        syncMenuSource.includes('onClick: openSyncToStoreModal'),
      '「同步」菜单应只包含商品管理权限可见的同步到分店',
    )
    assert(
      syncMenuSource.includes('icon={<SyncOutlined />}'),
      '「同步」菜单按钮应使用静态同步图标',
    )
    const toolsMenuSource = headerSource.slice(headerSource.indexOf("label={t('common.listToolbar.tools', '工具')}"))
    assert(
      toolsMenuSource.includes("key: 'integrityCheck'") &&
        toolsMenuSource.includes('onClick: () => { setIntegrityVisible(true); setIntegrityResult(null) }') &&
        toolsMenuSource.includes("key: 'batchImageUpdate'") &&
        toolsMenuSource.includes('onClick: openImageBatch') &&
        toolsMenuSource.includes("key: 'categoryManagement'") &&
        toolsMenuSource.includes('onClick: handleOpenCategoryModal') &&
        toolsMenuSource.match(/visible: canManagePosProducts,/g)?.length === 2,
      '「工具」菜单应包含一致性检查，以及受商品管理权限控制的图片批量修改和商品分类管理',
    )
    assert(
      headerSource.match(/type="primary"/g)?.length === 1 &&
        headerSource.includes('{canCreateStoreProducts && (\n            <Button type="primary" icon={<PlusOutlined />} onClick={openCreateModal}>'),
      '页头唯一的主按钮应为受创建权限控制的「创建商品」',
    )

    const toolbarStart = pageSource.indexOf('<div ref={toolbarRef} className="pos-products-toolbar">')
    const tableStart = pageSource.indexOf('<MeasuredTable metricId="pos-admin.product-management.table-1"')
    const toolbarSource = pageSource.slice(toolbarStart, tableStart)
    const filterRowSource = toolbarSource.slice(0, toolbarSource.indexOf('<ActiveFilterBar'))
    const selectionStart = toolbarSource.indexOf('<SelectionActionBar selectedCount={selectedRowKeys.length} onClearSelection={() => setSelectedRowKeys([])}>')
    const selectionSource = toolbarSource.slice(selectionStart, toolbarSource.indexOf('</SelectionActionBar>'))
    assert(toolbarStart >= 0 && tableStart > toolbarStart && selectionStart > 0, '勾选后操作条应位于 toolbarRef 内、表格之前，以便参与表格高度计算')
    for (const handler of ['onClick={openBatchEdit}', 'handleBatchEnable(true)', 'handleBatchEnable(false)', 'onClick={handleBatchTranslate}', 'onClick={handlePushToHq}']) {
      assertEqual(pageSource.split(handler).length - 1, 1, `${handler} 应只出现一次`)
      assert(selectionSource.includes(handler), `${handler} 应位于勾选后操作条内`)
      assert(!filterRowSource.includes(handler), `${handler} 不应留在筛选行`)
    }
    assert(
      selectionSource.includes('disabled={!selectedRowKeys.length || categoryLoadFailed}') &&
        selectionSource.includes("<Popconfirm title={t('posAdmin.products.confirmBatchEnable', '确认启用选中的商品？')} onConfirm={() => handleBatchEnable(true)}>") &&
        selectionSource.includes("<Popconfirm title={t('posAdmin.products.confirmBatchDisable', '确认禁用选中的商品？')} onConfirm={() => handleBatchEnable(false)}>") &&
        selectionSource.includes('<Button size="small" danger disabled={!selectedRowKeys.length}>') &&
        selectionSource.includes('disabled={!selectedRowKeys.length || translating}') &&
        selectionSource.includes('disabled={!selectedRowKeys.length || pushToHqLoading || pushToHqModalOpen}'),
      '勾选后操作条应保留批量编辑、批量启用/禁用确认、批量翻译和发送到 HQ 原有的 loading 与禁用条件',
    )
    assert(
      !selectionSource.includes('handleSyncSelectedFromHq') && !selectionSource.includes('{isAdmin && ('),
      '勾选后操作条不应再保留仅 Admin 可见的「从HQ同步选中」',
    )
    assert(
      selectionSource.lastIndexOf('{canManagePosProducts && (') < selectionSource.indexOf('onClick={handlePushToHq}') &&
        selectionSource.indexOf('{canManagePosProducts && (') < selectionSource.indexOf('onClick={openBatchEdit}'),
      '批量编辑类操作和发送到 HQ 应受商品管理权限控制',
    )
  })
  if (toolbarLayoutFailure) failures.push(toolbarLayoutFailure)

  const instantFilterFailure = await runTest('下拉筛选应选完即查，已生效条件应以标签展示并可逐个移除', () => {
    const applySource = pageSource.slice(
      pageSource.indexOf('const applyToolbarFilterChange = () => {'),
      pageSource.indexOf('const handleSupplierFilterChange'),
    )
    assert(
      applySource.includes('resetProductTableScroll()') &&
        applySource.includes('setPage(1)') &&
        applySource.includes('setSelectedRowKeys([])') &&
        !applySource.includes('setQueryVersion'),
      '选完即查应回到第一页、清空选择并归零滚动，且不再 bump queryVersion（生效态变化已会触发 loadData）',
    )
    const handlerPairs: Array<[string, string, string]> = [
      ['const handleSupplierFilterChange', 'setSupplierCodeInput(value)', 'setSupplierCode(value)'],
      ['const handleCategoryFilterChange', 'setCategoryGuidInput(guid)', 'setCategoryGuid(guid)'],
      ['const handleWarehouseCategoryFilterChange', 'setWarehouseCategoryGuidInput(guid)', 'setWarehouseCategoryGuid(guid)'],
      ['const handleActiveFilterChange', 'setIsActiveFilterInput(value)', 'setIsActiveFilter(value)'],
      ['const handleSetFilterChange', 'setIsSetFilterInput(value)', 'setIsSetFilter(value)'],
    ]
    for (const [handler, inputSetter, appliedSetter] of handlerPairs) {
      const start = pageSource.indexOf(handler)
      const handlerSource = pageSource.slice(start, pageSource.indexOf('\n  }\n', start))
      assert(
        start >= 0 &&
          handlerSource.includes(inputSetter) &&
          handlerSource.includes(appliedSetter) &&
          handlerSource.includes('applyToolbarFilterChange()'),
        `${handler} 应同时写输入态和生效态并立即生效`,
      )
    }
    for (const binding of [
      'onChange={handleSupplierFilterChange}',
      'onChange={handleCategoryFilterChange}',
      'onChange={handleWarehouseCategoryFilterChange}',
      'onChange={handleActiveFilterChange}',
      'onChange={handleSetFilterChange}',
      'onChange={handleStoreRecordCountModeChange}',
    ]) {
      assert(pageSource.includes(binding), `筛选下拉应绑定 ${binding}`)
    }
    const storeModeStart = pageSource.indexOf('const handleStoreRecordCountModeChange')
    const storeModeSource = pageSource.slice(storeModeStart, pageSource.indexOf('\n  }\n', storeModeStart))
    assertSourceOrder(storeModeSource, 'setStoreRecordCountModeInput(value)', "if (value === 'custom') return", '分店记录应先写输入态')
    assertSourceOrder(storeModeSource, "if (value === 'custom') return", 'setStoreRecordCountMode(value)', '选择自定义范围不应立即生效或请求')
    assert(
      storeModeSource.includes('getPresetStoreRecordCountRange(value)') &&
        storeModeSource.includes('setStoreRecordCountMin(presetRange.min)') &&
        storeModeSource.includes('setStoreRecordCountMax(presetRange.max)') &&
        storeModeSource.includes('applyToolbarFilterChange()'),
      '全部/有记录/无记录应按预设范围立即生效',
    )
    assert(
      pageSource.match(/onPressEnter=\{handleSearch\}/g)?.length === 3,
      '关键词与自定义范围的最小/最大值输入应在回车时按查询生效（沿用 handleSearch 校验）',
    )

    assert(
      pageSource.includes('<ActiveFilterBar items={activeFilterItems} onClearAll={handleReset} />') &&
        pageSource.includes('...buildToolbarFilterChips(') &&
        pageSource.includes('...buildColumnFilterChips(columnFilters, productColumnFilterMeta, {') &&
        pageSource.includes("source: 'toolbar',\n      onRemove: () => removeToolbarFilter(chip.key),") &&
        pageSource.includes("source: 'column',\n      onRemove: () => removeColumnFilter(chip.filterKey),"),
      '已生效筛选条应汇总生效态顶部筛选与列头筛选，清空全部复用 handleReset',
    )
    const chipInputStart = pageSource.indexOf('...buildToolbarFilterChips(')
    const chipInputSource = pageSource.slice(chipInputStart, pageSource.indexOf('supplierName:', chipInputStart))
    assert(
      chipInputSource.includes('keyword,') &&
        chipInputSource.includes('isActive: isActiveFilter,') &&
        chipInputSource.includes('storeRecordCountMode,') &&
        !chipInputSource.includes('Input'),
      '已生效筛选条只能读取生效态，不能读取尚未查询的输入态',
    )
    const removeSource = pageSource.slice(
      pageSource.indexOf('const removeToolbarFilter = (key: ToolbarFilterKey) => {'),
      pageSource.indexOf('const removeColumnFilter'),
    )
    for (const [inputSetter, appliedSetter] of [
      ["setKeywordInput('')", "setKeyword('')"],
      ['setSupplierCodeInput(undefined)', 'setSupplierCode(undefined)'],
      ['setCategoryGuidInput(undefined)', 'setCategoryGuid(undefined)'],
      ['setWarehouseCategoryGuidInput(undefined)', 'setWarehouseCategoryGuid(undefined)'],
      ['setIsActiveFilterInput(undefined)', 'setIsActiveFilter(undefined)'],
      ['setIsSetFilterInput(undefined)', 'setIsSetFilter(undefined)'],
      ["setStoreRecordCountModeInput('all')", "setStoreRecordCountMode('all')"],
    ]) {
      assert(removeSource.includes(inputSetter) && removeSource.includes(appliedSetter), `移除标签应同时清空 ${inputSetter} 与 ${appliedSetter}`)
    }
    assert(removeSource.includes('applyToolbarFilterChange()'), '移除标签后应立即重新查询')
    const removeColumnSource = pageSource.slice(
      pageSource.indexOf('const removeColumnFilter'),
      pageSource.indexOf('const productColumnFilterMeta'),
    )
    assert(
      removeColumnSource.includes('delete next[filterKey]') && removeColumnSource.includes('applyToolbarFilterChange()'),
      '移除列头筛选标签应只删掉对应列条件并重新查询',
    )
  })
  if (instantFilterFailure) failures.push(instantFilterFailure)

  const toolbarHeightFailure = await runTest('工具栏高度变化时应重算表格高度', () => {
    const layoutStart = pageSource.indexOf('const tbarH = toolbarRef.current?.getBoundingClientRect().height || 0')
    const layoutSource = pageSource.slice(layoutStart, pageSource.indexOf('}, [pageSize, total])', layoutStart))
    assert(
      layoutSource.includes('new ResizeObserver(() => calc())') &&
        layoutSource.includes('resizeObserver?.observe(toolbarRef.current)') &&
        layoutSource.includes('resizeObserver?.observe(pagerRef.current)') &&
        layoutSource.includes('resizeObserver?.disconnect()'),
      '已生效筛选条、勾选后操作条出现或消失时应通过 ResizeObserver 重算表格高度，并在卸载时断开',
    )
    const calcStart = pageSource.indexOf('const calc = () => {')
    const calcSource = pageSource.slice(calcStart, pageSource.indexOf('calc()', calcStart + 20))
    assert(
      calcSource.includes('wrapRef.current?.getBoundingClientRect().top') &&
        calcSource.includes('window.innerHeight - wrapTop - PRODUCT_LIST_BOTTOM_GAP') &&
        calcSource.includes('setProductListHeight(containerH)') &&
        pageSource.includes("height: productListHeight ?? 'calc(100vh - 200px)'"),
      '列表容器高度应按其在视口中的实际起点计算，不能只靠写死的 100vh 减估算值',
    )
    const pagerStart = pageSource.indexOf('ref={pagerRef}')
    const pagerSource = pageSource.slice(pagerStart, pageSource.indexOf('<Pagination', pagerStart))
    assert(
      !pagerSource.includes("t('posAdmin.products.selectedCount'") &&
        !pagerSource.includes("t('posAdmin.products.clearSelection'") &&
        pagerSource.includes("justifyContent: 'flex-end'"),
      '选中数量与取消选择已由勾选后操作条展示，分页栏不应重复显示',
    )
  })
  if (toolbarHeightFailure) failures.push(toolbarHeightFailure)

  const terminalResultFailure = await runTest('HQ 同步 job 的状态展示已移除，其余后台任务仍走共享超时提示', () => {
    assert(
      !pageSource.includes('readActiveProductHqSyncJob') &&
        !pageSource.includes('showActiveHqSyncJobStatus') &&
        !pageSource.includes('hqSyncInProgress') &&
        !pageSource.includes('hqSyncJobPartialSucceeded') &&
        !pageSource.includes('hqSyncJobSucceeded') &&
        !pageSource.includes('incrementalStartDateRequired'),
      '页面不应再保留商品 HQ 同步 job 的恢复、状态与结果展示',
    )
    assert(
      pageSource.includes('HqProductSyncPollingTimeoutError') &&
        pageSource.includes('showPollingTimeout()'),
      '同步到分店、图片批量修改等后台任务仍应由共享 poller 抛出超时错误并走统一超时提示',
    )
  })
  if (terminalResultFailure) failures.push(terminalResultFailure)

  if (failures.length > 0) {
    throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  }

  console.log('ProductManagement.hqSync.logic.test: ok')
}

await main()
