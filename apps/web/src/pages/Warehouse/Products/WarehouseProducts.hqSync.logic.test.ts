import { readFileSync } from 'node:fs'
import path from 'node:path'
import './WarehouseProducts.batchImageUrl.uiContract.test'
import {
  createWarehouseProductHqSyncJob,
  getWarehouseProductHqSyncJob,
  syncWarehouseProductsFromHq,
  type WarehouseProductListItem,
} from '../../../services/warehouseProductService'
import type { CurrentUser } from '../../../types/auth'
import { buildAccess } from '../../../utils/access'
import {
  ALL_PRODUCTS_FILTER_KEY,
  UNCATEGORIZED_PRODUCTS_FILTER_KEY,
} from '../Categories/categoryProductFilters'
import {
  buildCategoryQueryValue,
  buildComparableFilterTokens,
  buildRangeFilterTokens,
  buildTextFilterTokens,
  getSingleFilterValue,
  normalizeTableFilters,
  normalizeWarehouseProductSortField,
  parseComparableFilterTokens,
  parseTextFilterTokens,
  resolveCategoryFilterValueFromTableFilters,
  setFilterValues,
} from './columnFilters'
import {
  areWarehouseProductCodeSelectionsEqual,
  buildWarehouseProductHqPushPayload,
} from './hqPush'

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

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualText = JSON.stringify(actual)
  const expectedText = JSON.stringify(expected)
  if (actualText !== expectedText) {
    throw new Error(`${message}。Expected: ${expectedText}, received: ${actualText}`)
  }
}

async function assertRejects(execute: () => Promise<unknown>, expectedMessage: string, label: string) {
  try {
    await execute()
  } catch (error) {
    const actualMessage = error instanceof Error ? error.message : String(error)
    assertEqual(actualMessage, expectedMessage, label)
    return
  }

  throw new Error(`${label}。Expected promise to reject`)
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

function countOccurrences(source: string, text: string) {
  return source.split(text).length - 1
}

function extractSection(source: string, startText: string, endText: string) {
  const startIndex = source.indexOf(startText)
  assert(startIndex >= 0, `未找到代码片段：${startText}`)

  const endIndex = source.indexOf(endText, startIndex)
  assert(endIndex >= 0, `未找到结束片段：${endText}`)

  return source.slice(startIndex, endIndex)
}

const pageFile = path.resolve(process.cwd(), 'src/pages/Warehouse/Products/index.tsx')
const pageSource = readFileSync(pageFile, 'utf8')
const zhLocaleSource = readFileSync(path.resolve(process.cwd(), 'src/i18n/locales/zh.json'), 'utf8')
const enLocaleSource = readFileSync(path.resolve(process.cwd(), 'src/i18n/locales/en.json'), 'utf8')
const columnFiltersFile = path.resolve(process.cwd(), 'src/pages/Warehouse/Products/columnFilters.ts')
const columnFiltersSource = readFileSync(columnFiltersFile, 'utf8')
const categoryTreePickerFile = path.resolve(process.cwd(), 'src/pages/Warehouse/Products/CategoryTreePicker.tsx')
const categoryTreePickerSource = readFileSync(categoryTreePickerFile, 'utf8')

async function main() {
  const failures: string[] = []

  const pushPayloadFailure = await runTest('仓库商品发送 HQ 应携带页面商品资料与库存三价', () => {
    const products: WarehouseProductListItem[] = [
      {
        id: 'HB001',
        productCode: 'HB001',
        itemNumber: 'ITEM-001',
        name: '测试商品',
        nameEn: 'Test Product',
        barcode: '952700000001',
        localSupplierCode: 'SUP-AU',
        domesticPrice: 8.88,
        importPrice: 1.23,
        labelPrice: 4.99,
        productImage: 'https://example.com/product.jpg',
        isActive: true,
        productType: 0,
      },
    ]

    const payload = buildWarehouseProductHqPushPayload(
      products,
      ['HB001'],
      ['productName', 'inventoryDomesticPrice', 'inventoryImportPrice', 'inventoryOemPrice'],
      ['1001', '1002'],
    )

    assertDeepEqual(payload, {
      productCodes: ['HB001'],
      targetStoreCodes: ['1001', '1002'],
      items: [
        {
          productCode: 'HB001',
          localSupplierCode: 'SUP-AU',
          itemNumber: 'ITEM-001',
          productName: '测试商品',
          englishName: 'Test Product',
          barcode: '952700000001',
          imageUrl: 'https://example.com/product.jpg',
          domesticPrice: 8.88,
          importPrice: 1.23,
          oemPrice: 4.99,
          isNewProduct: false,
        },
      ],
      updateFields: ['productName', 'inventoryDomesticPrice', 'inventoryImportPrice', 'inventoryOemPrice'],
    }, '仓库 HQ payload 应保留完整商品候选、目标分店，并把 labelPrice 映射为 oemPrice')
  })
  if (pushPayloadFailure) failures.push(pushPayloadFailure)

  const pushSelectionRaceFailure = await runTest('发送 HQ 确认前后应复核最新选择集合', () => {
    assertEqual(
      areWarehouseProductCodeSelectionsEqual(['HB001', 'HB002'], ['HB002', 'HB001']),
      true,
      '同一选择集合不应因顺序变化被误判',
    )
    assertEqual(
      areWarehouseProductCodeSelectionsEqual(['HB001'], []),
      false,
      '弹窗期间列表刷新并清空选择时必须中止发送',
    )
    assertEqual(
      areWarehouseProductCodeSelectionsEqual(['HB001'], ['HB002']),
      false,
      '弹窗期间选择商品变化时必须中止发送',
    )
  })
  if (pushSelectionRaceFailure) failures.push(pushSelectionRaceFailure)

  const pushPermissionFailure = await runTest('发送 HQ 应只开放给 POS 商品管理权限', () => {
    const posManagerAccess = buildAccess(createCurrentUser({ permissions: ['PosProducts.Manage'] }))
    const warehouseOnlyAccess = buildAccess(createCurrentUser({ permissions: ['Warehouse.ManageProducts'] }))

    assertEqual(posManagerAccess.canManagePosProducts, true, 'PosProducts.Manage 应允许发送 HQ')
    assertEqual(warehouseOnlyAccess.canManagePosProducts, false, 'Warehouse.ManageProducts 不应扩大 HQ 跨库写权限')
  })
  if (pushPermissionFailure) failures.push(pushPermissionFailure)

  const pushToHqUiFailure = await runTest('仓库商品页应按 POS 商品管理权限提供发送 HQ 完整交互', () => {
    assert(
      pageSource.includes('pushProductsToHq') &&
        pageSource.includes('buildWarehouseProductHqPushPayload') &&
        pageSource.includes('PosHqPushModal') &&
        pageSource.includes('getPushToHqStoreOptions'),
      '页面应引入发送 HQ 服务、仓库 payload 映射、共享弹窗和分店选项服务',
    )
    assert(
      pageSource.includes('const [pushToHqLoading, setPushToHqLoading] = useState(false);') &&
        pageSource.includes('const pushToHqLoadingRef = useRef(false);') &&
        pageSource.includes('const [pushToHqModalOpen, setPushToHqModalOpen] = useState(false);') &&
        pageSource.includes('const [pushToHqStoreOptions, setPushToHqStoreOptions]'),
      '页面应维护发送 loading、即时锁、弹窗可见性和独立 HQ 分店选项',
    )

    const loadSection = extractSection(
      pageSource,
      'const loadPushToHqStoreOptions',
      'const handlePushToHq = async',
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
      '每次打开弹窗应重取最新 HQ 分店选项，并使用单飞加最新请求守卫忽略过期响应',
    )

    const openHandlerSection = extractSection(
      pageSource,
      'const handlePushToHq = async',
      'const handlePushToHqConfirm',
    )
    assert(
      openHandlerSection.includes('if (!access.canManagePosProducts)') &&
        openHandlerSection.includes('if (!selectedRowKeys.length)') &&
        openHandlerSection.includes('if (pushToHqLoadingRef.current || pushToHqModalOpen) return;') &&
        openHandlerSection.includes('pushToHqLoadingRef.current = true;') &&
        openHandlerSection.includes('setPushToHqModalOpen(true);') &&
        openHandlerSection.includes('await loadPushToHqStoreOptions();') &&
        openHandlerSection.indexOf('pushToHqLoadingRef.current = true;') <
          openHandlerSection.indexOf('await loadPushToHqStoreOptions();'),
      '发送处理应在即时锁内打开弹窗并获取最新 HQ 分店选项，避免重复打开',
    )

    const confirmSection = extractSection(
      pageSource,
      'const handlePushToHqConfirm',
      'const handlePushToHqCancel',
    )
    assert(
      confirmSection.indexOf('if (pushToHqLoadingRef.current || pushToHqConfirmLoadingRef.current) return;') <
          confirmSection.indexOf('pushToHqConfirmLoadingRef.current = true;') &&
        confirmSection.indexOf('pushToHqConfirmLoadingRef.current = true;') <
          confirmSection.indexOf('await pushProductsToHq(payload)') &&
        confirmSection.includes('pushToHqConfirmLoadingRef.current = false;') &&
        confirmSection.includes('if (!isMountedRef.current) return;') &&
        confirmSection.includes('selectedRowKeysRef.current.map(String)') &&
        confirmSection.includes('areWarehouseProductCodeSelectionsEqual(productCodes, currentProductCodes)') &&
        confirmSection.includes('buildWarehouseProductHqPushPayload(dataRef.current, currentProductCodes, updateFields, targetStoreCodes)') &&
        confirmSection.includes('await pushProductsToHq(payload)') &&
        confirmSection.includes('if (!showPushToHqResult(result)) return;') &&
        confirmSection.includes('setPushToHqModalOpen(false);') &&
        confirmSection.includes('setSelectedRowKeys([]);') &&
        confirmSection.includes('await refreshCurrentList();'),
      '确认提交应复核最新选择、发送含目标分店的仓库 payload，并仅在成功后关闭、清选、刷新',
    )
    assert(
      confirmSection.lastIndexOf('if (isMountedRef.current)') <
          confirmSection.indexOf('setPushToHqConfirmLoading(false);') &&
        confirmSection.lastIndexOf('if (isMountedRef.current)') <
          confirmSection.indexOf('setPushToHqLoading(false);'),
      '提交 finally 释放 loading 状态前必须检查组件是否已卸载',
    )
    const failureSection = extractSection(confirmSection, 'catch (error)', 'finally')
    assert(
      failureSection.includes('const errorResult = extractPushToHqErrorResult(error)') &&
        failureSection.includes('Modal.error({') &&
        !failureSection.includes('setSelectedRowKeys([])'),
      '失败时应保留商品、字段和分店选择，并显示后端返回的错误明细',
    )

    const cancelSection = extractSection(
      pageSource,
      'const handlePushToHqCancel',
      'const handleBatchToggleActive',
    )
    assert(
      cancelSection.includes('setPushToHqModalOpen(false);') &&
        cancelSection.includes('pushToHqLoadingRef.current = false;') &&
        cancelSection.includes('pushToHqStoreOptionsGuardRef.current.invalidate();') &&
        cancelSection.indexOf('if (pushToHqConfirmLoadingRef.current) return') <
          cancelSection.indexOf('pushToHqLoadingRef.current = false;') &&
        !cancelSection.includes('pushProductsToHq('),
      '取消只关闭弹窗、使过期选项响应失效并释放锁，且提交进行中不得释放锁',
    )

    // 发送到HQ 只对选中行生效，已从页头移到勾选后操作条。
    const toolbarSection = extractSection(
      pageSource,
      '<SelectionActionBar selectedCount={selectedRowKeys.length}',
      '</SelectionActionBar>',
    )
    assert(
      !extractSection(pageSource, "<PageContainer compact title={t('menu.warehouseProducts')}", '<div className="warehouse-products-layout">').includes('handlePushToHq'),
      '发送到HQ 不应再出现在页头',
    )
    // 2026-10 重设计：勾选条按钮按设计不带图标；文案改用本页 warehouseUi 键（原 posAdmin 键在全局语言包里缺失，英文界面会回退成中文）。
    assert(
      toolbarSection.includes('access.canManagePosProducts ?') &&
        toolbarSection.includes('loading={pushToHqLoading}') &&
        toolbarSection.includes('disabled={!selectedRowKeys.length || pushToHqLoading || pushToHqModalOpen}') &&
        toolbarSection.includes('onClick={() => void handlePushToHq()}') &&
        toolbarSection.includes("t('warehouseUi.products.pushToHq')"),
      '勾选后操作条的发送按钮应只对 POS 商品管理员显示，并正确绑定选择、loading 与点击行为',
    )
    const resultSection = extractSection(
      pageSource,
      'const showPushToHqResult = useCallback',
      'const loadPushToHqStoreOptions',
    )
    assert(
      resultSection.includes("if (errors.length || (result.failedCount ?? 0) > 0)") &&
        resultSection.includes('Modal.warning({') &&
        resultSection.includes('return false;') &&
        resultSection.includes('Modal.success({') &&
        resultSection.includes('return true;') &&
        resultSection.includes("t('posAdmin.products.pushToHqAffectedRows', 'HQ影响记录')") &&
        resultSection.includes("errors.join('\\n')"),
      '发送结果应复用商品管理页的成功、部分成功和 HQ 影响统计反馈',
    )
    assert(
      pageSource.includes('<PosHqPushModal') &&
        pageSource.includes('storeOptions={pushToHqStoreOptions}') &&
        pageSource.includes('onConfirm={handlePushToHqConfirm}') &&
        pageSource.includes('onCancel={handlePushToHqCancel}'),
      '页面应渲染共享弹窗并绑定 HQ 选项、确认与取消',
    )
    assert(
      zhLocaleSource.includes('"pushToHqSelectionChanged": "选中商品数据已变化，请重新选择后再试"') &&
        enLocaleSource.includes('"pushToHqSelectionChanged": "The selected product data changed. Please select the products again and retry."'),
      '选择变化提示应同时提供中英文翻译，不能让英文界面回退到中文',
    )
  })
  if (pushToHqUiFailure) failures.push(pushToHqUiFailure)

  const adminAccessFailure = await runTest('Admin 权限判断成立', () => {
    const access = buildAccess(
      createCurrentUser({
        roleNames: ['Admin'],
      }),
    )

    assertEqual(access.isAdmin, true, 'Admin 应被识别为管理员')
  })
  if (adminAccessFailure) failures.push(adminAccessFailure)

  const nonAdminAccessFailure = await runTest('非 Admin 权限不会显示同步按钮', () => {
    const access = buildAccess(
      createCurrentUser({
        roleNames: ['WarehouseStaff'],
      }),
    )

    assertEqual(access.isAdmin, false, 'WarehouseStaff 不应被识别为管理员')
  })
  if (nonAdminAccessFailure) failures.push(nonAdminAccessFailure)

  const shelfStatusTextFailure = await runTest('仓库商品状态文案应使用上架和下架', () => {
    assert(
      pageSource.includes("function getShelfStatusLabel(isActive: boolean") &&
        pageSource.includes("t('warehouse.onShelf', '上架')") &&
        pageSource.includes("t('warehouse.offShelf', '下架')"),
      '页面应通过 getShelfStatusLabel 统一仓库商品上下架文案',
    )

    const formModalSection = extractSection(
      pageSource,
      'function ProductFormModal',
      'function SetItemsModal',
    )
    assert(
      formModalSection.includes("label={t('warehouse.isListed')}") &&
        formModalSection.includes('checkedChildren={getShelfStatusLabel(true, t)}') &&
        formModalSection.includes('unCheckedChildren={getShelfStatusLabel(false, t)}'),
      '编辑弹窗状态字段应显示是否上架和上架/下架 Switch 文案',
    )

    const columnsSection = extractSection(
      pageSource,
      'const baseColumns = useMemo',
      'return (<>',
    )
    // 2026-10 重设计：行内上下架 Switch 去掉，状态列改为状态胶囊只读展示，上下架改在勾选条与 ⋯ 菜单里操作。
    assert(
      columnsSection.includes("<StatusPill tone={value ? 'green' : 'gray'}>{getShelfStatusLabel(value, t)}</StatusPill>") &&
        !columnsSection.includes('<Switch') &&
        !columnsSection.includes("t('warehouse.active')") &&
        !columnsSection.includes("t('warehouse.inactive')"),
      '主表状态列应显示上架/下架，不能继续使用启用/停用文案',
    )

    const batchSection = extractSection(
      pageSource,
      'const handleBatchToggleActive = async',
      'const handleToggleSingleActive',
    )
    const singleSection = extractSection(
      pageSource,
      'const handleToggleSingleActive = async',
      'const handleOpenSetItems',
    )
    assert(
      batchSection.includes('status: getShelfStatusLabel(nextIsActive, t)') &&
        singleSection.includes('status: getShelfStatusLabel(nextIsActive, t)'),
      '批量和单条状态成功提示应统一使用上架/下架文案',
    )
  })
  if (shelfStatusTextFailure) failures.push(shelfStatusTextFailure)

  const productTypeAndActionFailure = await runTest('仓库商品类型列和操作入口应区分普通套装多码', () => {
    assert(
      pageSource.includes('function getProductTypeTagColor(value: ProductType)') &&
        pageSource.includes('if (value === ProductType.SET) return') &&
        pageSource.includes('if (value === ProductType.MULTICODE) return') &&
        pageSource.includes('function canManageProductDetails(productType: ProductType)'),
      '页面应声明商品类型颜色和可管理类型判断',
    )

    const columnsSection = extractSection(
      pageSource,
      'const baseColumns = useMemo',
      'return (<>',
    )
    assert(
      columnsSection.includes("title: t('column.productType')") &&
        columnsSection.includes('dataIndex: \'productType\'') &&
        columnsSection.includes('<Tag color={getProductTypeTagColor(value)}>{getProductTypeLabel(value, t)}</Tag>'),
      '商品类型列应以 Tag 显示普通、套装和多码',
    )
    // 2026-10 重设计：行操作收敛为「编辑 + ⋯」，套装/多码管理入口在 ⋯ 菜单（buildRowActionItems）里。
    const rowActionSection = extractSection(
      pageSource,
      'const buildRowActionItems = ',
      'const draggableColumnKeys',
    )
    assert(
      rowActionSection.includes('canManageProductDetails(record.productType)') &&
        rowActionSection.includes('getProductDetailsActionLabel(record.productType, t)') &&
        rowActionSection.includes('getProductDetailsDisabledHint(t)') &&
        rowActionSection.includes('void handleOpenSetItems(record);') &&
        !rowActionSection.includes('record.productType === 1 ?'),
      '操作列应允许套装和多码进入管理入口，不能再只判断 productType === 1',
    )
    assert(
      pageSource.includes("t('warehouse.multiCodeManagement', '多码管理')") &&
        pageSource.includes("t('warehouse.normalProductNoDetails', '普通商品没有套装或多码明细')"),
      '多码商品和普通商品应有明确操作文案',
    )
  })
  if (productTypeAndActionFailure) failures.push(productTypeAndActionFailure)

  const productDetailsModalFailure = await runTest('套装和多码应复用明细弹窗但按类型显示标题和提示', () => {
    const modalSection = extractSection(
      pageSource,
      'function SetItemsModal',
      'export default function WarehouseProductsPage',
    )
    assert(
      modalSection.includes('title={getProductDetailsModalTitle(product, t)}') &&
        modalSection.includes('getProductDetailsHint(product?.productType, t)') &&
        modalSection.includes("t('warehouse.addMultiCodeDetail', '新增多码')"),
      '明细弹窗应按商品类型展示套装或多码标题、提示和新增按钮',
    )
    assert(
      pageSource.includes("t('warehouse.multiCodeDetailsTitle', '多码管理 - {{name}}'") &&
        pageSource.includes("t('warehouse.multiCodeEditHint', '多码商品可维护多码条码、价格和分店同步使用的明细。')"),
      '多码明细弹窗应有独立标题和说明文案',
    )
  })
  if (productDetailsModalFailure) failures.push(productDetailsModalFailure)

  const warehouseProductSetCodesFailure = await runTest('仓库套装明细弹窗应读取并保存 product-set-codes 明细', () => {
    assert(
      pageSource.includes("from '../../../services/multiCodeSetService'") &&
        pageSource.includes('getGridData as getSetCodeGridData') &&
        pageSource.includes('batchCreateSetCodes') &&
        pageSource.includes('batchUpdateBarcodes as batchUpdateSetBarcodes') &&
        pageSource.includes('batchUpdatePrices as batchUpdateSetPrices') &&
        pageSource.includes('batchDelete as batchDeleteSetCodes'),
      '仓库商品页应使用 product-set-codes 服务维护套装/多码明细',
    )
    assert(
      !pageSource.includes('getDomesticProductSetItems') &&
        !pageSource.includes('updateDomesticProductSetItems') &&
        !pageSource.includes('DomesticProductSetItem'),
      '仓库商品页不能继续引用国内采购 set-items 服务和类型',
    )

    const modalSection = extractSection(
      pageSource,
      'function SetItemsModal',
      'export default function WarehouseProductsPage',
    )
    assert(
      modalSection.includes('items: MulticodeSetItem[]') &&
        modalSection.includes("dataIndex: 'setItemNumber'") &&
        modalSection.includes("dataIndex: 'setBarcode'") &&
        modalSection.includes("dataIndex: 'setPurchasePrice'") &&
        modalSection.includes("dataIndex: 'setRetailPrice'") &&
        modalSection.includes("dataIndex: 'isActive'"),
      '弹窗列应使用 product-set-codes 的货号、条码、进货价、零售价和状态字段',
    )

    const openSection = extractSection(
      pageSource,
      'const handleOpenSetItems = async (record: WarehouseProductListItem) => {',
      'const handleSaveSetItems = async () => {',
    )
    assert(
      openSection.includes('getSetCodeGridData({ productCode: record.productCode') &&
        openSection.includes('setSetItemsDraft(result.items ?? [])'),
      '打开仓库套装弹窗时应按当前仓库商品 productCode 读取 product-set-codes grid',
    )

    const saveSection = extractSection(
      pageSource,
      'const handleSaveSetItems = async () => {',
      'const handleExport = async () => {',
    )
    assert(
      saveSection.includes('batchCreateSetCodes({') &&
        saveSection.includes('batchUpdateSetBarcodes({') &&
        saveSection.includes('batchUpdateSetPrices({') &&
        saveSection.includes('batchDeleteSetCodes({ ids: deletedSetCodeIds })'),
      '保存仓库套装弹窗时应分别处理新增、已有更新和删除的 product-set-codes 明细',
    )
    assert(
      saveSection.includes('invalidSetCodeItem') &&
        saveSection.includes("message.error(t('warehouse.invalidSetCodeDetail'") &&
        saveSection.includes('item.setBarcode?.trim()') &&
        saveSection.includes('item.setPurchasePrice === undefined') &&
        saveSection.includes('item.setRetailPrice === undefined'),
      '保存前应校验套装明细条码、进货价和零售价，避免空新增或清空字段静默失败',
    )
    assert(
      saveSection.indexOf('batchDeleteSetCodes({ ids: deletedSetCodeIds })') >
        saveSection.indexOf('batchUpdateSetStatus({'),
      '删除已有明细必须放在新增和更新之后，避免后续接口失败时先删数据',
    )
  })
  if (warehouseProductSetCodesFailure) failures.push(warehouseProductSetCodesFailure)

  const minOrderQuantityColumnFailure = await runTest('仓库商品列表应以 MinOrderQuantity 作为中包数列来源', () => {
    const columnsSection = extractSection(
      pageSource,
      'const baseColumns = useMemo',
      'return (<>',
    )

    assert(
      columnsSection.includes("title: t('warehouse.middlePackQuantity', '中包数')") &&
        columnsSection.includes("dataIndex: 'minOrderQuantity'"),
      '主表应新增中包数列，并绑定 WarehouseProduct.MinOrderQuantity 归一后的 minOrderQuantity',
    )
    assert(
      !columnsSection.includes("dataIndex: 'middlePackQty'"),
      '主表中包数列不能绑定 middlePackQty，避免与 MiddlePackQuantity 来源混淆',
    )
    const packingColumnSection = extractSection(
      columnsSection,
      "key: 'packingQty'",
      "key: 'volume'",
    )
    assert(
      packingColumnSection.includes("record.isPackingQtyFallback ? <Tag color=\"green\">{t('warehouse.warehouse')}</Tag> : <Tag color=\"gold\">{t('warehouse.domestic')}</Tag>"),
      '装箱数使用仓库回退值时应显示仓库来源，否则显示国内来源',
    )
  })
  if (minOrderQuantityColumnFailure) failures.push(minOrderQuantityColumnFailure)

  const batchEditFailure = await runTest('仓库商品页应支持按选中商品批量修改常用字段', () => {
    assert(
      pageSource.includes('createWarehouseProductBatchUpdateJob') &&
        pageSource.includes('createWarehouseProductBatchUpdateJobPoller'),
      '页面应引入仓库商品后台批量更新服务与轮询器',
    )
    assert(
      pageSource.includes('interface BatchEditFormValues') &&
        pageSource.includes('minOrderQuantity?: number'),
      '页面应声明批量编辑表单，并使用 minOrderQuantity 表示中包数',
    )

    const saveSection = extractSection(
      pageSource,
      'const submitBatchEdit = async',
      'const handleToggleSingleActive',
    )
    assert(
      saveSection.includes('MinOrderQuantity: values.minOrderQuantity') &&
        saveSection.includes('PackingQuantity: values.packingQuantity') &&
        saveSection.includes('createWarehouseProductBatchUpdateJob(items, options)') &&
        saveSection.includes('startBatchUpdateJobPolling(activeJob)'),
      '批量保存应把中包数提交为 MinOrderQuantity，并通过后台任务执行与轮询',
    )
    assert(
      saveSection.includes('只传用户填写的字段') &&
        saveSection.includes('WarehouseProduct.MinOrderQuantity'),
      '批量 payload 构造处应有中文注释说明中包数字段来源和避免误覆盖',
    )

    const toolbarSection = extractSection(
      pageSource,
      '<SelectionActionBar selectedCount={selectedRowKeys.length}',
      '</SelectionActionBar>',
    )
    assert(
      toolbarSection.includes("t('warehouse.batchEdit', '批量修改')") &&
        toolbarSection.includes('onClick={openBatchEdit}') &&
        toolbarSection.includes('disabled={!selectedRowKeys.length || batchEditSaving}'),
      '勾选后操作条应提供批量修改按钮',
    )

    const modalSection = extractSection(
      pageSource,
      '<Modal title={t(\'warehouse.batchEditTitle\'',
      '<ImportFromDomesticModal',
    )
    assert(
      modalSection.includes('name="domesticPrice"') &&
        modalSection.includes('name="oemPrice"') &&
        modalSection.includes('name="importPrice"') &&
        modalSection.includes('name="packingQuantity"') &&
        modalSection.includes('name="minOrderQuantity"') &&
        modalSection.includes('name="unitVolume"') &&
        modalSection.includes('name="isActive"'),
      '批量修改弹窗应包含价格、装箱数、中包数、体积和上下架字段',
    )
  })
  if (batchEditFailure) failures.push(batchEditFailure)

  const draggableColumnsFailure = await runTest('仓库商品表格应支持拖拽列头并持久化列顺序', () => {
    assert(
      pageSource.includes('DndContext') &&
        pageSource.includes('SortableContext') &&
        pageSource.includes('useSortable') &&
        pageSource.includes('horizontalListSortingStrategy'),
      '商品管理表头列拖拽应复用 @dnd-kit 横向排序能力',
    )
    // 2026-10 重设计改为「商品 / 供应商」组合列，v1 缓存的旧列顺序合并后会把新组合列排到最后，所以换用 v2 键。
    assert(
      pageSource.includes("const WAREHOUSE_PRODUCT_COLUMN_ORDER_STORAGE_KEY = 'hbweb_rv.warehouseProducts.columnOrder.v2'") &&
        pageSource.includes('localStorage.setItem(WAREHOUSE_PRODUCT_COLUMN_ORDER_STORAGE_KEY') &&
        pageSource.includes('mergeWarehouseProductColumnOrder('),
      '商品管理列顺序应保存到独立 localStorage key，并兼容列增删',
    )
    // 被「列设置」隐藏的列不渲染表头，SortableContext 只能放可见列，否则 dnd-kit 按下标计算位移会错位。
    assert(
      pageSource.includes('components={{ header: { cell: DraggableHeaderCell } }}') &&
        pageSource.includes('<SortableContext items={visibleColumnOrder} strategy={horizontalListSortingStrategy}>') &&
        pageSource.includes('<DndContext sensors={columnDragSensors} collisionDetection={closestCenter} onDragEnd={handleColumnDragEnd}>'),
      '商品管理表格应接入可拖拽表头 cell 与横向 SortableContext',
    )
    assert(
      pageSource.includes('const draggableColumnKeys = [...WAREHOUSE_PRODUCT_DEFAULT_COLUMN_ORDER]') &&
        pageSource.includes('rowSelection={{') &&
        !pageSource.includes("columnOrder.includes('selection')"),
      '商品管理选择列仍应由 rowSelection 管理，不能进入业务列拖拽顺序',
    )
  })
  if (draggableColumnsFailure) failures.push(draggableColumnsFailure)

  const defaultColumnOrderFailure = await runTest('仓库商品表格默认列顺序应按设计并支持列设置与重置列', () => {
    const defaultOrderSection = extractSection(
      pageSource,
      'const WAREHOUSE_PRODUCT_DEFAULT_COLUMN_ORDER',
      '] as const',
    )
    // 2026-10 重设计：序号列去掉；货号、图片、名称合并为「商品」列，国内/澳洲供应商合并为「供应商」列；
    // 低频列按业务含义插在相关常驻列旁边，默认隐藏、在「列设置」里勾选显示。
    const expectedOrder = [
      "'product'",
      "'nameEn'",
      "'name'",
      "'barcode'",
      "'categoryName'",
      "'productType'",
      "'supplier'",
      "'localSupplierCode'",
      "'domesticPrice'",
      "'importPrice'",
      "'labelPrice'",
      "'minOrderQuantity'",
      "'packingQty'",
      "'volume'",
      "'locationCodes'",
      "'isActive'",
      "'supplyUpdatedAt'",
      "'updatedAt'",
      "'updatedBy'",
      "'action'",
    ]
    let lastIndex = -1
    for (const key of expectedOrder) {
      const nextIndex = defaultOrderSection.indexOf(key)
      assert(nextIndex > lastIndex, `默认列顺序应包含并按设计排列 ${key}`)
      lastIndex = nextIndex
    }
    assert(
      !defaultOrderSection.includes("'selection'") && !defaultOrderSection.includes("'rowNumber'"),
      '默认列顺序不能包含 selection（选择列仍由 rowSelection 管理），也不再有序号列',
    )
    const optionalSection = extractSection(pageSource, 'const WAREHOUSE_PRODUCT_OPTIONAL_COLUMN_KEYS', '] as const')
    for (const key of ["'nameEn'", "'name'", "'barcode'", "'categoryName'", "'productType'", "'localSupplierCode'", "'minOrderQuantity'", "'packingQty'", "'volume'", "'supplyUpdatedAt'", "'updatedAt'", "'updatedBy'"]) {
      assert(optionalSection.includes(key), `低频列 ${key} 应可在列设置里勾选显示`)
    }
    for (const key of ["'product'", "'supplier'", "'domesticPrice'", "'importPrice'", "'labelPrice'", "'locationCodes'", "'isActive'", "'action'"]) {
      assert(!optionalSection.includes(key), `常驻列 ${key} 不应出现在可选列里`)
    }
    assert(
      pageSource.includes('const WAREHOUSE_PRODUCT_DEFAULT_VISIBLE_OPTIONAL_COLUMNS: string[] = [];') &&
        pageSource.includes("const WAREHOUSE_PRODUCT_VISIBLE_COLUMNS_STORAGE_KEY = 'hbweb_rv.warehouseProducts.visibleColumns.v1'") &&
        pageSource.includes('localStorage.setItem(WAREHOUSE_PRODUCT_VISIBLE_COLUMNS_STORAGE_KEY') &&
        pageSource.includes('filterWarehouseProductVisibleColumnOrder('),
      '低频列默认隐藏，勾选结果记到独立 localStorage key，渲染时过滤未勾选的可选列',
    )

    const columnsSection = extractSection(
      pageSource,
      'const baseColumns = useMemo',
      'const draggableColumnKeys',
    )
    let lastColumnIndex = -1
    for (const key of expectedOrder) {
      const nextIndex = columnsSection.indexOf(`key: ${key}`)
      assert(nextIndex > lastColumnIndex, `baseColumns 应按默认顺序声明 ${key}，避免默认顺序依赖历史代码顺序`)
      lastColumnIndex = nextIndex
    }
    assert(
      pageSource.includes('const draggableColumnKeys = [...WAREHOUSE_PRODUCT_DEFAULT_COLUMN_ORDER]') &&
        pageSource.includes('mergeWarehouseProductColumnOrder(current.length ? current : savedOrder, WAREHOUSE_PRODUCT_DEFAULT_COLUMN_ORDER)'),
      '列顺序初始化应以显式默认顺序为准，并兼容 localStorage 旧缓存',
    )

    const resetSection = extractSection(
      pageSource,
      'const handleResetColumnOrder = () => {',
      'const orderedColumns = useMemo',
    )
    assert(
      resetSection.includes('localStorage.removeItem(WAREHOUSE_PRODUCT_COLUMN_ORDER_STORAGE_KEY)') &&
        resetSection.includes('localStorage.removeItem(WAREHOUSE_PRODUCT_VISIBLE_COLUMNS_STORAGE_KEY)') &&
        resetSection.includes('setColumnOrder([...WAREHOUSE_PRODUCT_DEFAULT_COLUMN_ORDER])') &&
        resetSection.includes('setVisibleOptionalColumns([...WAREHOUSE_PRODUCT_DEFAULT_VISIBLE_OPTIONAL_COLUMNS])') &&
        resetSection.includes('选择列仍由 Ant Design rowSelection 管理'),
      '重置列逻辑应清除列顺序与显示列缓存，恢复默认业务列顺序与显示列，并保留中文注释说明选择列边界',
    )
    // 「重置列」从筛选行收进「列设置」弹层：列顺序或显示列改过默认时才可用。
    const columnSettingsSource = readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/Products/ColumnSettingsButton.tsx'), 'utf8')
    assert(
      columnSettingsSource.includes("t('warehouse.resetColumns', '重置列')") &&
        columnSettingsSource.includes('disabled={!canReset} onClick={onReset}') &&
        pageSource.includes('onReset={handleResetColumnOrder}') &&
        pageSource.includes('canReset={isColumnOrderCustomized || isColumnVisibilityCustomized}'),
      '列设置弹层应提供重置列按钮，并仅在列顺序或显示列自定义后启用',
    )
    assert(
      pageSource.includes('rowSelection={{') &&
        !pageSource.includes("WAREHOUSE_PRODUCT_DEFAULT_COLUMN_ORDER = ['selection'"),
      '重置列功能不能改变 rowSelection 管理选择列的方式',
    )
  })
  if (defaultColumnOrderFailure) failures.push(defaultColumnOrderFailure)

  const supplierColumnDisplayFailure = await runTest('仓库商品供应商列应区分国内供应商和澳洲供应商名称显示', () => {
    const columnsSection = extractSection(
      pageSource,
      'const baseColumns = useMemo',
      'const draggableColumnKeys',
    )
    // 2026-10 重设计：默认显示「供应商」组合列（第一行国内、第二行澳洲），排序与列头筛选沿用国内供应商；
    // 澳洲供应商独立列保留为可选列（排序、列头筛选）。
    const domesticSupplierSection = extractSection(
      columnsSection,
      "key: 'supplier'",
      "key: 'localSupplierCode'",
    )
    const australianSupplierSection = extractSection(
      columnsSection,
      "key: 'localSupplierCode'",
      "key: 'domesticPrice'",
    )

    assert(
      domesticSupplierSection.includes("title: t('column.supplier')") &&
        domesticSupplierSection.includes("dataIndex: 'domesticSupplierCode'") &&
        domesticSupplierSection.includes("...enumFilterProps('domesticSupplierCode'") &&
        domesticSupplierSection.includes('sorter: true') &&
        domesticSupplierSection.includes("t('warehouse.domestic')") &&
        domesticSupplierSection.includes("t('warehouseUi.products.supplierAustralia')"),
      '供应商组合列应以 domesticSupplierCode 排序和筛选，并分国内、澳洲两行显示',
    )
    assert(
      australianSupplierSection.includes("title: t('column.australianSupplier', '澳洲供应商')") &&
        australianSupplierSection.includes("dataIndex: 'localSupplierCode'") &&
        australianSupplierSection.includes('sorter: true'),
      '澳洲供应商列应绑定 localSupplierCode，不能显示国内供应商字段',
    )
    assert(
      domesticSupplierSection.includes('record.domesticSupplierName || record.domesticSupplierCode') &&
        domesticSupplierSection.includes('record.localSupplierName || localSupplierNameMap[record.localSupplierCode || \'\'] || record.localSupplierCode') &&
        australianSupplierSection.includes('record.localSupplierName || localSupplierNameMap[record.localSupplierCode || \'\'] || record.localSupplierCode'),
      '国内供应商应优先显示名称；澳洲供应商应优先显示名称，并在行数据缺名称时用活跃供应商映射兜底',
    )
  })
  if (supplierColumnDisplayFailure) failures.push(supplierColumnDisplayFailure)

  const localSupplierFallbackFailure = await runTest('仓库商品澳洲供应商列应使用活跃供应商名称兜底', () => {
    assert(
      pageSource.includes("import { getActiveLocalSuppliers as getActiveAustralianSuppliers } from '../../../services/localSupplierService'"),
      '页面应从澳洲供应商服务导入活跃供应商列表，并使用别名避免和国内供应商混淆',
    )
    assert(
      pageSource.includes('const [localSupplierNameMap, setLocalSupplierNameMap] = useState<Record<string, string>>({})'),
      '页面应维护澳洲供应商代码到名称的兜底映射',
    )
    assert(
      pageSource.includes('getActiveAustralianSuppliers()') &&
        pageSource.includes('setLocalSupplierNameMap(') &&
        pageSource.includes('map[item.localSupplierCode] = item.name'),
      '页面加载时应读取活跃澳洲供应商并建立代码到名称映射',
    )

    const columnsSection = extractSection(
      pageSource,
      'const baseColumns = useMemo',
      'const draggableColumnKeys',
    )
    const australianSupplierSection = extractSection(
      columnsSection,
      "key: 'localSupplierCode'",
      "key: 'updatedAt'",
    )

    assert(
      australianSupplierSection.includes('record.localSupplierName || localSupplierNameMap[record.localSupplierCode || \'\'] || record.localSupplierCode'),
      '澳洲供应商列应按行内名称、活跃供应商名称映射、供应商代码的顺序显示',
    )
    assert(
      pageSource.includes('表格接口只返回澳洲供应商代码时，用活跃供应商列表补齐名称'),
      '兜底逻辑应有中文注释说明原因',
    )
  })
  if (localSupplierFallbackFailure) failures.push(localSupplierFallbackFailure)

  const categoryColumnFailure = await runTest('仓库商品表格应显示分类名称并悬浮展示完整路径', () => {
    const columnsSection = extractSection(
      pageSource,
      'const baseColumns = useMemo',
      'const draggableColumnKeys',
    )
    const categoryColumn = extractSection(
      columnsSection,
      "key: 'categoryName'",
      "key: 'minOrderQuantity'",
    )

    assert(categoryColumn.includes("title: t('column.category')"), '分类列应使用 column.category 文案')
    assert(categoryColumn.includes("dataIndex: 'categoryName'"), '分类列应绑定 categoryName')
    assert(categoryColumn.includes('renderWarehouseProductCategoryCell(record, categoryLookup, i18n.language)'), '分类列应使用分类展示 helper')
    assert(pageSource.includes('function renderWarehouseProductCategoryCell'), '页面应提供分类单元格 helper')
    assert(pageSource.includes('getWarehouseProductCategoryTooltip(record, categoryLookup, language)'), '分类 Tooltip 应优先读取完整路径 helper')
    assert(pageSource.includes('const categoryLookup = useMemo(() => buildWarehouseCategoryLookup(categories), [categories])'), '页面应基于分类树建立 GUID 和分类名到完整路径的映射')
    assert(pageSource.includes('buildWarehouseCategoryLookup') && pageSource.includes('WarehouseCategoryLookup'), '页面应复用可测试的分类路径 lookup helper')
    assert(categoryTreePickerSource.includes('formatWarehouseCategoryNodeName(node, language)'), '分类树节点应按当前语言显示名称')
    assert(pageSource.includes('buildFilterCategoryOptions(categories, t, i18n.language)'), '分类筛选下拉应使用当前语言显示分类名')
    assert(pageSource.includes("import CategoryTreePicker from './CategoryTreePicker'"), '批量分类弹窗应复用带查询的分类树组件')
    assert(pageSource.includes('setCategoryExpandedKeys(collectCategoryExpandedKeys(categories, 1));'), '批量分类弹窗每次打开应默认展开到一级分类')
    assert(pageSource.includes('<CategoryTreePicker categories={categories}') && pageSource.includes('maxHeight={420}'), '批量分类树应使用当前语言查询组件构建')
    assert(pageSource.includes('selectedTargetCategoryPath || formatWarehouseCategoryNodeName(selectedTargetCategory, i18n.language)'), '批量分类目标提示应显示当前语言完整路径')
    assert(pageSource.includes('<Tooltip title={tooltipTitle}'), '分类名称应通过 Tooltip 展示完整路径')
    assert(pageSource.includes('className="warehouse-products-category-cell"'), '分类名称应挂载紧凑样式 class')
    assert(pageSource.includes('record.categoryName ||') && pageSource.includes("'--'"), '分类列缺失名称时应显示 --')
    const batchCategorySaveSection = extractSection(
      pageSource,
      'const handleBatchCategorySave = async () => {',
      // 批量修改保存可带下架供货说明参数，锚点只匹配函数名前缀。
      'const handleBatchEditSave = async (',
    )
    assert(
      batchCategorySaveSection.includes('await batchAssignProducts(targetCategoryGuid, selectedProductCodes)') &&
        batchCategorySaveSection.includes('setData((items) =>') &&
        batchCategorySaveSection.includes('selectedProductCodeSet.has(item.productCode)') &&
        batchCategorySaveSection.includes('warehouseCategoryGUID: targetCategoryGuid') &&
        batchCategorySaveSection.includes('categoryName: selectedTargetCategory') &&
        batchCategorySaveSection.includes('formatWarehouseCategoryNodeName(selectedTargetCategory, i18n.language)') &&
        !batchCategorySaveSection.includes('void loadData({ page })'),
      '仓库商品批量分类保存成功后应本地更新当前页分类，不应重新查询商品表格',
    )
    assert(
      categoryTreePickerSource.includes('function filterCategoryTree') &&
        categoryTreePickerSource.includes('buildSearchText(node, language, parentPath).includes(keyword)') &&
        categoryTreePickerSource.includes('children: childResult.nodes') &&
        categoryTreePickerSource.includes('const visibleExpandedKeys = keyword ? searchResult.expandedKeys : expandedKeys') &&
        categoryTreePickerSource.includes("placeholder={t('warehouse.categories.searchPlaceholder'"),
      '共享分类树组件应支持查询分类名和父级路径，并在搜索时自动展开命中路径',
    )
  })
  if (categoryColumnFailure) failures.push(categoryColumnFailure)

  const categoryManagementFailure = await runTest('仓库商品页应复用分类管理弹窗并联动批量分类目标', () => {
    assert(
      pageSource.includes("import ContainerCategoryManageModal from '../ContainerDetail/ContainerCategoryManageModal'") &&
        pageSource.includes('resolveContainerCategorySelectionAfterRefresh') &&
        pageSource.includes('resolveContainerCategoryTargetAfterMutation'),
      '仓库商品页应复用货柜明细的分类管理弹窗和目标联动逻辑',
    )
    assert(
      pageSource.includes('const [categoryManageOpen, setCategoryManageOpen] = useState(false);'),
      '仓库商品页应维护独立的分类管理弹窗开关',
    )

    const openBatchCategorySection = extractSection(
      pageSource,
      'const openBatchCategory = () => {',
      'const handleBatchCategorySave = async () => {',
    )
    assert(
      openBatchCategorySection.includes('setTargetCategoryGuid((current) => findWarehouseCategory(categories, current)?.categoryGUID);') &&
        !openBatchCategorySection.includes('setTargetCategoryGuid(undefined);'),
      '打开批量分类时应保留分类树中仍存在的管理弹窗目标',
    )

    const categoryMutationSection = extractSection(
      pageSource,
      'const handleCategoryMutationCommitted = (change: ContainerCategoryChange) => {',
      'const openBatchCategory = () => {',
    )
    assert(
      categoryMutationSection.includes('resolveContainerCategoryTargetAfterMutation(current, change)') &&
        categoryMutationSection.includes('setCategories(tree);') &&
        categoryMutationSection.includes('setCategoryExpandedKeys(firstLevelExpandedKeys);') &&
        categoryMutationSection.includes('setCategoryFilterExpandedKeys(firstLevelExpandedKeys);') &&
        categoryMutationSection.includes('resolveContainerCategorySelectionAfterRefresh('),
      '分类写入及刷新后应同步批量目标、批量分类树和顶部筛选树',
    )

    // 2026-10 重设计：「管理分类」从页头挪到左侧分类面板头部；仍在本页打开分类管理弹窗，分类树改动后面板即时刷新。
    const manageButtonSection = extractSection(
      pageSource,
      'onManage={access.canManageWarehouseCategories ?',
      '\n',
    )
    const categoryPanelSource = readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/Products/CategoryFilterPanel.tsx'), 'utf8')
    assert(
      manageButtonSection.includes('() => setCategoryManageOpen(true)') &&
        !manageButtonSection.includes('selectedRowKeys') &&
        !manageButtonSection.includes('disabled=') &&
        categoryPanelSource.includes('{onManage ? (') &&
        categoryPanelSource.includes("t('containers.actions.manageCategories', '管理分类')") &&
        categoryPanelSource.includes('onClick={onManage}'),
      '管理分类入口应仅受分类管理权限控制，且无需勾选商品',
    )
    assert(
      !extractSection(pageSource, "<PageContainer compact title={t('menu.warehouseProducts')}", '<div className="warehouse-products-layout">').includes('setCategoryManageOpen'),
      '管理分类不应再出现在页头',
    )

    const categoryManageModalSection = extractSection(
      pageSource,
      '{access.canManageWarehouseCategories ? (<ContainerCategoryManageModal',
      '<ImportFromDomesticModal',
    )
    assert(
      categoryManageModalSection.includes('open={categoryManageOpen}') &&
        categoryManageModalSection.includes('activeTargetCategoryGuid={targetCategoryGuid}') &&
        categoryManageModalSection.includes('onMutationCommitted={handleCategoryMutationCommitted}') &&
        categoryManageModalSection.includes('onCategoriesChanged={handleCategoriesChanged}'),
      '分类管理弹窗应收到当前批量目标及两个联动回调',
    )
  })
  if (categoryManagementFailure) failures.push(categoryManagementFailure)

  const compactTableFailure = await runTest('仓库商品主表应使用紧凑行高、媒体尺寸和列宽', () => {
    // 2026-10 重设计：表格样式从页面内联 <style> 迁到页面级 warehouseProducts.css（普通 CSS + 页面前缀类名）。
    const pageCssSource = readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/Products/warehouseProducts.css'), 'utf8')
    assert(
      pageSource.includes("import './warehouseProducts.css';") && !pageSource.includes('<style>'),
      '页面应引入页面级 CSS，不再在组件里内联 <style>',
    )
    assert(
      pageCssSource.includes('height: 60px;') && pageCssSource.includes('max-height: 60px;'),
      '商品管理主表行高应压缩到紧凑值 60px',
    )
    assert(
      pageCssSource.includes('.warehouse-products-table .ant-table-thead > tr > th,') &&
        pageCssSource.includes('padding: 6px 8px !important') &&
        pageCssSource.includes('.warehouse-products-table .ant-table-column-title') &&
        pageCssSource.includes('-webkit-line-clamp: 2') &&
        pageCssSource.includes('.warehouse-products-table .ant-table-filter-column') &&
        pageCssSource.includes('.warehouse-products-table .ant-table-filter-trigger'),
      '商品管理主表应使用紧凑单元格 padding，且表头标题、排序和筛选图标应稳定排列',
    )
    assert(
      pageCssSource.includes('.warehouse-products-thumb') &&
        pageCssSource.includes('width: 40px;') &&
        pageCssSource.includes('height: 40px;') &&
        pageCssSource.includes('max-height: 42px !important'),
      '商品缩略图和条码预览应使用紧凑尺寸，减少行内占用空间',
    )
    assert(
      pageCssSource.includes('font-variant-numeric: tabular-nums;') &&
        pageCssSource.includes('justify-content: flex-end;'),
      '价格等数字列应右对齐并使用等宽数字',
    )

    const columnsSection = extractSection(
      pageSource,
      'const baseColumns = useMemo',
      'return (<>',
    )
    const productCellSection = extractSection(pageSource, 'const renderProductCell = ', 'const renderSupplyNoticeSummary = ')
    assert(
      columnsSection.includes("key: 'product'") &&
        columnsSection.includes('width: 248') &&
        productCellSection.includes('<ProductListImage src={record.productImage} size={40}') &&
        columnsSection.includes("key: 'supplier'") &&
        columnsSection.includes('width: 108') &&
        columnsSection.includes("key: 'isActive'") &&
        columnsSection.includes('width: 104') &&
        columnsSection.includes("key: 'domesticPrice'") &&
        columnsSection.includes('width: 80') &&
        columnsSection.includes("key: 'packingQty'") &&
        columnsSection.includes('width: 108') &&
        columnsSection.includes("key: 'minOrderQuantity'") &&
        columnsSection.includes("dataIndex: 'minOrderQuantity'") &&
        columnsSection.includes('width: 96') &&
        columnsSection.includes("key: 'updatedAt'") &&
        columnsSection.includes('width: 156') &&
        columnsSection.includes("key: 'action'") &&
        columnsSection.includes('width: 60'),
      '商品、供应商、状态、价格、装箱数、中包数、更新时间与操作列应使用紧凑列宽，且中包数仍绑定 minOrderQuantity',
    )
    // 默认可见列宽之和（含选择列 36）应不超过 1440 宽下列表区约 926px，保证默认视图不横向滚动。
    const defaultVisibleWidth = 36 + 220 + 120 + 80 + 88 + 88 + 92 + 112 + 60
    assert(defaultVisibleWidth <= 926, `默认可见列宽合计 ${defaultVisibleWidth} 超出 1440 宽下的列表区`)
    assert(
      columnsSection.includes('BarcodePreview value={value} textMaxWidth={150} compactCopy') &&
        pageSource.includes('scroll={{ x: tableScrollX, y: 620 }}') &&
        pageSource.includes('const tableScrollX = useMemo(() => orderedColumns.reduce(') &&
        pageSource.includes('WAREHOUSE_PRODUCT_SELECTION_COLUMN_WIDTH,'),
      '条码列保留条码图；表格横向滚动宽度取可见列宽之和（含选择列）',
    )
    assert(
      pageSource.includes('components={{ header: { cell: DraggableHeaderCell } }}') &&
        pageSource.includes('rowSelection={{') &&
        pageSource.includes('const orderedColumns = useMemo'),
      '紧凑显示不能移除拖拽列头、rowSelection 或 orderedColumns',
    )
  })
  if (compactTableFailure) failures.push(compactTableFailure)

  const mainTablePaginationFailure = await runTest('仓库商品主表默认每页 100 且仅提供大分页选项', () => {
    const tableSection = extractSection(
      pageSource,
      'pagination={{',
      '}} onChange={(pagination: TablePaginationConfig',
    )

    assert(
      pageSource.includes('const WAREHOUSE_PRODUCTS_DEFAULT_PAGE_SIZE = 100') &&
        pageSource.includes("const WAREHOUSE_PRODUCTS_PAGE_SIZE_OPTIONS = ['50', '100', '200', '500', '1000']"),
      '仓库商品主表应集中声明默认分页 100 和 50/100/200/500/1000 分页选项',
    )
    assert(
      pageSource.includes('const [pageSize, setPageSize] = useState(WAREHOUSE_PRODUCTS_DEFAULT_PAGE_SIZE);'),
      '仓库商品主表 pageSize 初始值应使用默认分页常量 100',
    )
    assert(
      tableSection.includes('pageSizeOptions: WAREHOUSE_PRODUCTS_PAGE_SIZE_OPTIONS') &&
        tableSection.includes('showSizeChanger: true,'),
      '仓库商品主表分页下拉应只使用 50/100/200/500/1000 这些选项，并保留切换入口',
    )
    assert(
      pageSource.includes('virtual') &&
        pageSource.includes('scroll={{ x: tableScrollX, y: 620 }}') &&
        pageSource.includes('const result = await getWarehouseProductsTable(query);'),
      '分页调整应保留现有虚拟表格、固定滚动高度和异步服务端分页请求',
    )
  })
  if (mainTablePaginationFailure) failures.push(mainTablePaginationFailure)

  // HQ → HBweb 的「从HQ同步库存」已于 2026-09-29 停用（后端返回 410），页面不得再保留入口、job 与轮询逻辑。
  const adminOnlyButtonFailure = await runTest('页面不再提供从HQ同步库存入口', () => {
    for (const removed of [
      "t('warehouse.hqSync', '从HQ同步库存')",
      "key: 'hqSync'",
      'handleSyncWarehouseProductsFromHq',
      'syncingFromHq',
      'activeHqSyncJob',
      'createWarehouseProductHqSyncJob',
      'getWarehouseProductHqSyncJob',
      'createWarehouseProductHqSyncJobPoller',
      'WAREHOUSE_PRODUCT_HQ_SYNC_ACTIVE_JOB_STORAGE_KEY',
      'warehouse.products.activeHqSyncJob',
      'startHqSyncJobPolling',
      'showHqSyncJobResult',
      'buildHqSyncResultDescription',
    ]) {
      assert(!pageSource.includes(removed), `页面不应再包含 HQ → HBweb 库存同步代码：${removed}`)
    }
  })
  if (adminOnlyButtonFailure) failures.push(adminOnlyButtonFailure)

  const loadingFailure = await runTest('「价格与同步」菜单只保留更新分店价格这一同步入口且使用静态图标', () => {
    // 2026-10 重设计：原「同步」「价格」两个菜单合并为「价格与同步」，同步项仍只有写 HQ 的更新分店价格。
    const syncMenuSection = extractSection(
      pageSource,
      "<ToolbarMenuButton label={t('warehouseUi.products.priceAndSync')}",
      "<ToolbarMenuButton label={t('common.listToolbar.importExport'",
    )
    assert(
      syncMenuSection.includes('icon: <CloudSyncOutlined />') &&
        !syncMenuSection.includes('loading={') &&
        !syncMenuSection.includes("key: 'hqSync'") &&
        syncMenuSection.includes("key: 'storePriceSync'"),
      '价格与同步菜单应只保留写 HQ 的更新分店价格',
    )
  })
  if (loadingFailure) failures.push(loadingFailure)

  const successRefreshFailure = await runTest('刷新当前列表仍应经过 mounted gate 并走 current loader', () => {
    const refreshSection = extractSection(
      pageSource,
      'const refreshCurrentList',
      'const stopBatchUpdateJobPolling',
    )

    assert(
      refreshSection.includes('if (!isMountedRef.current) {') &&
      refreshSection.includes('loadDataRef.current?.(overrides)'),
      '刷新当前列表应在 mounted gate 后通过 current loader 执行',
    )
  })
  if (successRefreshFailure) failures.push(successRefreshFailure)

  const serviceUrlFailure = await runTest('同步服务应使用正确的 URL、POST 方法，并在后端返回失败时抛出 message', async () => {
    const originalFetch = globalThis.fetch
    let capturedUrl = ''
    let capturedInit: RequestInit | undefined

    try {
      globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
        capturedUrl = String(input)
        capturedInit = init

        return new Response(JSON.stringify({
          success: true,
          data: {
            isSuccess: true,
            message: '同步完成',
          },
        }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }) as typeof fetch

      await syncWarehouseProductsFromHq()

      assertEqual(capturedUrl, '/api/react/v1/product-warehouse/sync-from-hq', '同步服务应命中既定接口地址')
      assertEqual(capturedInit?.method, 'POST', '同步服务应使用 POST 方法')

      globalThis.fetch = (async () => new Response(JSON.stringify({
        success: false,
        message: '后端返回同步失败',
      }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })) as typeof fetch

      await assertRejects(
        () => syncWarehouseProductsFromHq(),
        '后端返回同步失败',
        '后端 success=false 时应抛出后端 message',
      )

      globalThis.fetch = (async () => new Response(JSON.stringify({
        success: true,
        message: '外层成功但同步失败',
        data: {
          isSuccess: false,
          message: '内层同步事务失败',
        },
      }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })) as typeof fetch

      await assertRejects(
        () => syncWarehouseProductsFromHq(),
        '内层同步事务失败',
        '外层 success=true 但 data.isSuccess=false 时应抛出内层 message',
      )
    } finally {
      globalThis.fetch = originalFetch
    }
  })
  if (serviceUrlFailure) failures.push(serviceUrlFailure)

  const jobServiceFailure = await runTest('后台 job 服务应使用创建和查询 URL', async () => {
    const originalFetch = globalThis.fetch
    const capturedUrls: string[] = []
    const capturedMethods: Array<string | undefined> = []

    try {
      globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
        capturedUrls.push(String(input))
        capturedMethods.push(init?.method)

        return new Response(JSON.stringify({
          success: true,
          data: {
            jobId: 'warehouse-job-1',
            status: 'Running',
            createdAt: '2026-06-04T00:00:00Z',
          },
        }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }) as typeof fetch

      await createWarehouseProductHqSyncJob({ operationId: 'warehouse-products-hq-sync' })
      await getWarehouseProductHqSyncJob('warehouse-job-1')

      assertEqual(
        capturedUrls[0],
        '/api/react/v1/product-warehouse/sync-from-hq/jobs',
        '创建后台 job 应命中新接口地址',
      )
      assertEqual(capturedMethods[0], 'POST', '创建后台 job 应使用 POST 方法')
      assertEqual(
        capturedUrls[1],
        '/api/react/v1/product-warehouse/sync-from-hq/jobs/warehouse-job-1',
        '查询后台 job 应命中 job 查询接口地址',
      )
      assertEqual(capturedMethods[1], 'GET', '查询后台 job 应使用 GET 方法')
    } finally {
      globalThis.fetch = originalFetch
    }
  })
  if (jobServiceFailure) failures.push(jobServiceFailure)

  const columnFilterHelperFailure = await runTest('仓库商品列头筛选 helper 应保持运行时语义', () => {
    assertDeepEqual(
      setFilterValues({ domesticSupplierCode: ['CN-001'] }, 'domesticSupplierCode', ['  ', undefined]),
      {},
      '空值应移除对应列头筛选',
    )
    assertDeepEqual(
      buildRangeFilterTokens(' 5 ', 10),
      ['gte:5', 'lte:10'],
      '数字范围应生成后端识别的 gte/lte token',
    )
    assertDeepEqual(buildTextFilterTokens('contains', 'Clock'), ['__filter:contains:Clock'], '文本包含应生成命名空间 contains token')
    assertDeepEqual(buildTextFilterTokens('eq', 'HB001'), ['__filter:eq:HB001'], '文本等于应生成命名空间 eq token')
    assertDeepEqual(buildTextFilterTokens('starts', 'HB'), ['__filter:starts:HB'], '文本开头是应生成命名空间 starts token')
    assertDeepEqual(buildTextFilterTokens('ends', '001'), ['__filter:ends:001'], '文本结尾是应生成命名空间 ends token')
    assertDeepEqual(parseTextFilterTokens(['Clock']), { mode: 'contains', value: 'Clock' }, '旧文本裸值应兼容为 contains')
    assertDeepEqual(parseTextFilterTokens(['starts:HB']), { mode: 'contains', value: 'starts:HB' }, '旧文本保留前缀字面值')
    assertDeepEqual(parseTextFilterTokens(['__filter:starts:HB']), { mode: 'starts', value: 'HB' }, '文本 token 应能还原模式和值')
    assertDeepEqual(buildComparableFilterTokens('eq', { value: 12 }), ['__filter:eq:12'], '数字等于应生成命名空间 eq token')
    assertDeepEqual(buildComparableFilterTokens('range', { min: 5, max: 10 }), ['gte:5', 'lte:10'], '数字范围应生成 gte/lte token')
    assertDeepEqual(buildComparableFilterTokens('gte', { value: 8 }), ['gte:8'], '数字大于等于应生成 gte token')
    assertDeepEqual(buildComparableFilterTokens('lte', { value: 9 }), ['lte:9'], '数字小于等于应生成 lte token')
    assertDeepEqual(parseComparableFilterTokens(['18']), { mode: 'eq', value: '18', min: '', max: '' }, '旧数字裸值应兼容为 eq')
    assertDeepEqual(parseComparableFilterTokens(['__filter:eq:18']), { mode: 'eq', value: '18', min: '', max: '' }, '数字 eq token 应能还原模式和值')
    assertDeepEqual(parseComparableFilterTokens(['gte:2026-06-01', 'lte:2026-06-16']), {
      mode: 'range',
      min: '2026-06-01',
      max: '2026-06-16',
      value: '',
    }, '日期范围 token 应能还原为 range 模式')
    assertDeepEqual(
      normalizeTableFilters({
        name: [' Clock '],
        labelPrice: ['gte:2', 'lte:9'],
        categoryName: [UNCATEGORIZED_PRODUCTS_FILTER_KEY],
        domesticSupplierCode: ['CN-001'],
      }),
      {
        productName: ['Clock'],
        oemPrice: ['gte:2', 'lte:9'],
        domesticSupplierCode: ['CN-001'],
      },
      '普通列头筛选应映射后端 key，分类不应混入普通 Filters',
    )
    assertEqual(
      resolveCategoryFilterValueFromTableFilters({ categoryName: [UNCATEGORIZED_PRODUCTS_FILTER_KEY] }),
      UNCATEGORIZED_PRODUCTS_FILTER_KEY,
      '分类列头值应单独解析',
    )
    assertDeepEqual(
      buildCategoryQueryValue(UNCATEGORIZED_PRODUCTS_FILTER_KEY),
      { categoryGuid: undefined, uncategorizedOnly: true },
      '未分类列头应转成顶层 UncategorizedOnly',
    )
    assertDeepEqual(
      buildCategoryQueryValue('cat-runtime-001'),
      { categoryGuid: 'cat-runtime-001', uncategorizedOnly: false },
      '具体分类列头应转成顶层 CategoryGuids 查询值',
    )
    assertDeepEqual(
      buildCategoryQueryValue(ALL_PRODUCTS_FILTER_KEY),
      { categoryGuid: undefined, uncategorizedOnly: false },
      '全部商品列头应清空分类顶层字段',
    )
    assertEqual(getSingleFilterValue(['true']), 'true', '单选筛选应能同步回顶部筛选')
    assertEqual(getSingleFilterValue(['true', 'false']), undefined, '多选筛选不应强行同步为顶部单值')
  })
  if (columnFilterHelperFailure) failures.push(columnFilterHelperFailure)

  const columnFilterStateFailure = await runTest('仓库商品页应维护列头后端筛选状态并区分分类顶层字段', () => {
    assert(
      pageSource.includes('const [columnFilters, setColumnFilters] = useState<WarehouseProductColumnFilters>({})') &&
        pageSource.includes('const mergedFilters = overrides.filters ?? columnFilters;') &&
        pageSource.includes("filters: Object.keys(mergedFilters).length ? mergedFilters : undefined") &&
        pageSource.includes('列头筛选走后端 Filters，分类仍走顶层字段'),
      '页面应维护 columnFilters 状态，并在 buildGridQuery 中把普通列头筛选发到后端 Filters',
    )
    // 2026-10 重设计：状态改由状态页签设置；「更多筛选」新增澳洲供应商，与列头筛选共用同一份 columnFilters。
    assert(
      pageSource.includes("const nextFilters = setFilterValues(columnFilters, 'domesticSupplierCode'") &&
        pageSource.includes("const nextFilters = setFilterValues(columnFilters, 'productType'") &&
        pageSource.includes("const nextFilters = setFilterValues(columnFilters, 'isActive'") &&
        pageSource.includes("const nextFilters = setFilterValues(columnFilters, 'localSupplierCode'") &&
        countOccurrences(pageSource, 'setColumnFilters(nextFilters);') === 4,
      '顶部供应商、商品类型、状态页签和澳洲供应商筛选变化时应同步 columnFilters，避免旧列头值残留',
    )
  })
  if (columnFilterStateFailure) failures.push(columnFilterStateFailure)

  const topCategoryTreeFilterFailure = await runTest('仓库商品页分类筛选应使用左侧分类面板，窄屏回到可折叠分类树下拉', () => {
    const topFilterSection = extractSection(
      pageSource,
      '<Input className="warehouse-products-search" value={searchText}',
      '<Select value={productType}',
    )

    assert(
      pageSource.includes('TreeSelect') &&
        pageSource.includes('buildFilterCategoryTreeOptions') &&
        pageSource.includes('const [categoryFilterExpandedKeys, setCategoryFilterExpandedKeys] = useState<string[]>([])') &&
        pageSource.includes("const [categoryFilterSearchText, setCategoryFilterSearchText] = useState('')") &&
        pageSource.includes('const hasCategoryFilterSearchText = categoryFilterSearchText.trim().length > 0;') &&
        pageSource.includes('const categoryFilterTreeOptions = useMemo(() => buildFilterCategoryTreeOptions(categories, t, i18n.language)'),
      '顶部分类筛选应引入 TreeSelect，并使用树形分类 options、搜索状态与独立展开状态',
    )
    assert(
      pageSource.includes('const firstLevelExpandedKeys = collectCategoryExpandedKeys(tree, 1);') &&
        pageSource.includes('setCategoryExpandedKeys(firstLevelExpandedKeys);') &&
        pageSource.includes('setCategoryFilterExpandedKeys(firstLevelExpandedKeys);'),
      '加载分类树后应同时初始化批量分类树和顶部筛选树的一级展开状态',
    )
    assert(
      topFilterSection.includes('<TreeSelect') &&
        topFilterSection.includes('treeData={categoryFilterTreeOptions}') &&
        topFilterSection.includes('searchValue={categoryFilterSearchText}') &&
        topFilterSection.includes('onSearch={setCategoryFilterSearchText}') &&
        topFilterSection.includes('treeExpandedKeys={hasCategoryFilterSearchText ? undefined : categoryFilterExpandedKeys}') &&
        topFilterSection.includes('if (!hasCategoryFilterSearchText)') &&
        topFilterSection.includes('treeNodeFilterProp="searchText"'),
      '顶部分类控件应绑定 treeData、搜索字段，并在搜索时让 TreeSelect 自动展开命中路径',
    )
    assert(
      topFilterSection.includes('allowClear') &&
        topFilterSection.includes('setCategoryFilterValue(value || ALL_PRODUCTS_FILTER_KEY);') &&
        topFilterSection.includes("setCategoryFilterSearchText('');") &&
        topFilterSection.includes('<span className="warehouse-products-category-select">'),
      '窄屏分类树下拉清空后应回到全部商品并清空搜索词',
    )
    // 左侧分类面板：「全部商品」「未分类」固定入口 + 可搜索分类树，点选即查；未分类经 buildCategoryQueryValue 转为 UncategorizedOnly。
    const categoryPanelSource = readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/Products/CategoryFilterPanel.tsx'), 'utf8')
    const categoryChangeSection = extractSection(pageSource, 'const handleCategoryFilterChange = (value: string) => {', 'const statusTabKey')
    assert(
      pageSource.includes('<CategoryFilterPanel') &&
        pageSource.includes('onChange={handleCategoryFilterChange}') &&
        pageSource.includes('expandedKeys={categoryFilterExpandedKeys}') &&
        categoryChangeSection.includes('setCategoryFilterValue(value);') &&
        categoryChangeSection.includes('void loadData({ page: 1, ...buildCategoryQueryValue(value) });') &&
        categoryPanelSource.includes('onClick={() => onChange(ALL_PRODUCTS_FILTER_KEY)}') &&
        categoryPanelSource.includes('onClick={() => onChange(UNCATEGORIZED_PRODUCTS_FILTER_KEY)}') &&
        categoryPanelSource.includes('filterCategoryTree(categories, keyword, language)') &&
        categoryTreePickerSource.includes('export function filterCategoryTree'),
      '左侧分类面板应提供全部商品、未分类入口与可搜索分类树（复用分类树搜索逻辑），选中后立即按分类查询第 1 页',
    )
    const pageCssSource = readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/Products/warehouseProducts.css'), 'utf8')
    assert(
      pageCssSource.includes('container-type: inline-size;') &&
        pageCssSource.includes('@container warehouse-products (max-width: 1079px)'),
      '列表容器变窄时应收起分类面板、显示工具栏分类下拉',
    )
  })
  if (topCategoryTreeFilterFailure) failures.push(topCategoryTreeFilterFailure)

  const tableChangeColumnFilterFailure = await runTest('表格 onChange 应读取列头 filters 并重查第一页', () => {
    const tableSection = extractSection(
      pageSource,
      'onChange={(pagination: TablePaginationConfig, filters: Record<string, FilterValue | null>, sorter:',
      '}/>',
    )

    // 2026-10 重设计：低频列可被「列设置」隐藏，AntD filters 不再含这些列，需沿用原筛选与当前分类。
    assert(
      tableSection.includes('const nextColumnFilters = keepHiddenColumnFilters(normalizeTableFilters(filters), filters, columnFilters);') &&
        tableSection.includes('const nextCategoryFilterValue = resolveCategoryFilterValueFromTableFilters(filters, categoryFilterValue);') &&
        tableSection.includes('setColumnFilters(nextColumnFilters);'),
      '表格 onChange 应接收 AntD filters，并转换后回写 columnFilters，隐藏列的条件不能被清掉',
    )
    assert(
      tableSection.includes("page: extra.action === 'paginate' ? pagination.current || 1 : 1,") &&
        tableSection.includes('filters: nextColumnFilters,') &&
        tableSection.includes('...categoryQuery,'),
      '列头筛选或排序变化后应带 filters 重查数据，并在非分页场景回到第一页',
    )
    assert(
      tableSection.includes('const categoryQuery = buildCategoryQueryValue(nextCategoryFilterValue);') &&
        tableSection.includes('setCategoryFilterValue(nextCategoryFilterValue);'),
      '分类列头变化时应转成顶层分类查询字段，而不是混入普通 Filters',
    )
  })
  if (tableChangeColumnFilterFailure) failures.push(tableChangeColumnFilterFailure)

  const priceColumnSortFailure = await runTest('零售价和进口价列应使用后端价格字段排序', () => {
    const columnsSection = extractSection(
      pageSource,
      'const baseColumns = useMemo',
      'const draggableColumnKeys',
    )
    const importPriceSection = extractSection(
      columnsSection,
      "key: 'importPrice'",
      "key: 'labelPrice'",
    )
    const labelPriceSection = extractSection(
      columnsSection,
      "key: 'labelPrice'",
      "key: 'isActive'",
    )
    const tableSection = extractSection(
      pageSource,
      'onChange={(pagination: TablePaginationConfig, filters: Record<string, FilterValue | null>, sorter:',
      '}/>',
    )

    assert(
      importPriceSection.includes('sorter: true') && labelPriceSection.includes('sorter: true'),
      '进口价和零售价列均应启用 AntD 服务端排序入口',
    )
    assert(
      columnFiltersSource.includes('export function normalizeWarehouseProductSortField(') &&
        tableSection.includes('normalizeWarehouseProductSortField(nextSorter?.field, sortField)'),
      '表格排序应在请求前把 labelPrice 规范为后端 oemPrice 字段',
    )
    assertEqual(
      normalizeWarehouseProductSortField('labelPrice', 'createdAt'),
      'oemPrice',
      '零售价排序字段应映射为后端 OEMPrice 字段',
    )
    assertEqual(
      normalizeWarehouseProductSortField('importPrice', 'createdAt'),
      'importPrice',
      '进口价排序字段应保持不变',
    )
    assertEqual(
      normalizeWarehouseProductSortField(undefined, 'createdAt'),
      'createdAt',
      'AntD 未返回字段时应保留当前排序字段',
    )
  })
  if (priceColumnSortFailure) failures.push(priceColumnSortFailure)

  const columnFilterUiFailure = await runTest('仓库商品表格应为文本数字日期枚举列接入列头过滤 UI', () => {
    const columnsSection = extractSection(
      pageSource,
      'const baseColumns = useMemo',
      'const draggableColumnKeys',
    )

    assert(
      pageSource.includes('const renderColumnFilterPanel = (content: ReactNode, onApply: () => void, onReset: () => void) =>') &&
        pageSource.includes('统一列头筛选面板骨架') &&
        pageSource.includes('warehouse-products-column-filter-panel') &&
        pageSource.includes('warehouse-products-column-filter-body') &&
        pageSource.includes('warehouse-products-column-filter-actions') &&
        !pageSource.includes('style={{ width: 112 }}'),
      '文本、数字和日期列头筛选应复用统一面板，不能回退到窄 Select 下拉',
    )
    assert(
      pageSource.includes('const buildTextFilterDropdown = (filterKey: string, placeholder: string) =>') &&
        pageSource.includes('const buildNumberRangeFilterDropdown = (filterKey: string) =>') &&
        pageSource.includes('const buildDateRangeFilterDropdown = (filterKey: string) =>') &&
        pageSource.includes('textFilterModeOptions') &&
        pageSource.includes('comparableFilterModeOptions'),
      '页面应提供文本、数字和日期列头筛选 helper，并显示匹配方式选择',
    )
    assert(
      columnFiltersSource.includes('const TABLE_FILTER_KEY_MAP: Record<string, string> = {') &&
        columnFiltersSource.includes("name: 'productName'") &&
        columnFiltersSource.includes("labelPrice: 'oemPrice'") &&
        columnFiltersSource.includes("product: 'itemNumber'") &&
        columnFiltersSource.includes("supplier: 'domesticSupplierCode'"),
      'normalizeTableFilters 应显式维护列 key 到后端 filter key 的映射（含商品、供应商组合列）',
    )
    assert(
        columnsSection.includes("...textFilterProps('itemNumber'") &&
        columnsSection.includes("...textFilterProps('productName'") &&
        columnsSection.includes("...textFilterProps('nameEn'") &&
        columnsSection.includes("...textFilterProps('barcode'") &&
        columnsSection.includes("...textFilterProps('locationCodes'"),
      '货号、商品名、英文名、条码和货位列应接入文本列头筛选',
    )
    assert(
      columnsSection.includes("...numberRangeFilterProps('minOrderQuantity')") &&
        columnsSection.includes("...numberRangeFilterProps('domesticPrice')") &&
        columnsSection.includes("...numberRangeFilterProps('importPrice')") &&
        columnsSection.includes("...numberRangeFilterProps('oemPrice')") &&
        columnsSection.includes("...numberRangeFilterProps('packingQty')") &&
        columnsSection.includes("...numberRangeFilterProps('volume')") &&
        columnsSection.includes("...dateRangeFilterProps('updatedAt')"),
      '中包数、价格、装箱数、体积和更新时间列应接入数字/日期列头筛选',
    )
    assert(
      columnsSection.includes("...enumFilterProps('domesticSupplierCode'") &&
        columnsSection.includes("...enumFilterProps('localSupplierCode'") &&
        columnsSection.includes("...enumFilterProps('isActive'") &&
        columnsSection.includes("...enumFilterProps('productType'") &&
        columnsSection.includes('filters: categoryColumnFilterOptions') &&
        columnsSection.includes('filteredValue: categoryFilterValue === ALL_PRODUCTS_FILTER_KEY ? null : [categoryFilterValue]'),
      '供应商、状态、商品类型和分类列应暴露 filters / filteredValue 形式的列头过滤 UI',
    )
    assert(
      columnsSection.includes("key: 'name'") &&
        columnsSection.includes("dataIndex: 'name'") &&
        columnsSection.includes("...textFilterProps('productName'") &&
        columnsSection.includes("key: 'labelPrice'") &&
        columnsSection.includes("dataIndex: 'labelPrice'") &&
        columnsSection.includes("...numberRangeFilterProps('oemPrice')"),
      '商品名和 OEM 列应保留原列 key，同时继续使用后端 productName / oemPrice filter key',
    )
    assert(
      columnsSection.includes("key: 'locationCodes'") &&
        columnsSection.includes("dataIndex: 'locationCodes'") &&
        columnsSection.includes("t('location.location', '货位')"),
      '货位列应使用 locationCodes 作为列 key/dataIndex，并复用货位翻译文案',
    )
  })
  if (columnFilterUiFailure) failures.push(columnFilterUiFailure)

  const resetColumnFilterFailure = await runTest('重置查询应清空列头筛选状态', () => {
    const resetSection = extractSection(
      pageSource,
      'const handleResetFilters = () => {',
      'const handleCategoryFilterChange',
    )

    // 2026-10 重设计去掉筛选行的「重置」按钮，「清空全部」是唯一入口，语义不变。
    assert(
      resetSection.includes('setColumnFilters({});') &&
        resetSection.includes('filters: {},') &&
        resetSection.includes('clearSearchDebounce();') &&
        resetSection.includes("setSubmittedSearchText('');") &&
        resetSection.includes("searchText: '',") &&
        !resetSection.includes('setColumnOrder') &&
        !pageSource.includes('onClick={handleResetFilters}') &&
        pageSource.includes('onClearAll={handleResetFilters}'),
      '「清空全部」时应清空 columnFilters 与已提交关键词、取消未触发的防抖查询，并按空 Filters 重查列表，且不改动列顺序',
    )
  })
  if (resetColumnFilterFailure) failures.push(resetColumnFilterFailure)

  const priceCurrencyFailure = await runTest('仓库商品国内价显示 ¥ 且其他价格显示 $', () => {
    const currencyHelper = extractSection(
      pageSource,
      'function getWarehouseProductPricePrefix',
      'type SupplierSelectOption',
    )
    assert(
      currencyHelper.includes("field === 'minOrderQuantity'") &&
        currencyHelper.includes("return field === 'domesticPrice' ? '¥' : '$'"),
      '中包数量不应显示货币符号；国内价应使用 ¥，进口价和零售价应使用 $',
    )

    const inlineHandlers = extractSection(
      pageSource,
      'const handleStartInlineEdit',
      'const showPushToHqResult',
    )
    assert(
      inlineHandlers.includes('const pricePrefix = getWarehouseProductPricePrefix(field);') &&
        inlineHandlers.includes('prefix={pricePrefix}') &&
        inlineHandlers.includes("formatPrice(value, pricePrefix ?? '$')"),
      '价格单元格的只读文本与双击编辑器应使用同一货币符号',
    )
  })
  if (priceCurrencyFailure) failures.push(priceCurrencyFailure)

  const inlineEditFailure = await runTest('仓库商品四列应支持无确认弹窗的双击单字段保存', () => {
    assert(
      pageSource.includes('patchWarehouseProduct') &&
        pageSource.includes("type WarehouseProductInlineEditField = 'minOrderQuantity' | 'domesticPrice' | 'importPrice' | 'labelPrice'"),
      '页面应引入单字段 PATCH 服务并限定四个可编辑字段',
    )
    const productEditorOnlyAccess = buildAccess(createCurrentUser({ permissions: ['Products.Edit'] }))
    assertEqual(productEditorOnlyAccess.canWriteProduct, true, 'Products.Edit 仍可用于既有商品编辑功能')
    assertEqual(productEditorOnlyAccess.isAdmin || productEditorOnlyAccess.isWarehouseManager, false, '普通商品编辑权限不满足 PATCH 角色约束')
    assert(
      pageSource.includes('const { access, currentUser } = useAuthStore();') &&
        pageSource.includes("roleName === 'Admin' || roleName === 'WarehouseManager'") &&
        pageSource.includes('if (!canInlineEditWarehouseProduct || inlineSaveLockRef.current) return;') &&
        pageSource.includes("className={`warehouse-products-inline-value${canInlineEditWarehouseProduct ? ' is-editable' : ''}`}") &&
        pageSource.includes("title={canInlineEditWarehouseProduct ? t('warehouse.doubleClickToEdit', '双击编辑') : undefined}"),
      '内联编辑入口必须与后端 Admin/WarehouseManager 角色保持一致',
    )
    assert(
      pageSource.includes('const inlineSaveLockRef = useRef<string | null>(null);') &&
        pageSource.includes('if (inlineSaveLockRef.current) return;') &&
        pageSource.includes('inlineSaveLockRef.current = cellKey;') &&
        pageSource.includes('inlineSaveLockRef.current = null;'),
      'Enter 与失焦必须共享即时提交锁，避免同一单元格重复请求',
    )

    const inlineHandlers = extractSection(
      pageSource,
      'const handleStartInlineEdit',
      'const showPushToHqResult',
    )
    assert(
      inlineHandlers.includes('value === null || value === undefined') &&
        inlineHandlers.includes('value < 0') &&
        inlineHandlers.includes('patchWarehouseProduct(cell.productCode') &&
        inlineHandlers.includes('await refreshCurrentList();'),
      '共享保存函数应忽略空值、阻止负数、调用 PATCH，并在成功后刷新当前分页',
    )
    assert(
      inlineHandlers.includes("message.success(t('common.saveSuccess', '保存成功'))") &&
        inlineHandlers.includes('message.error(') &&
        !inlineHandlers.includes('Modal.confirm') &&
        !inlineHandlers.includes('Popconfirm'),
      '单元格保存只能使用 success/error 轻提示，不能显示确认弹窗',
    )
    const inlineFailureSection = extractSection(inlineHandlers, 'catch (error)', 'finally')
    assert(
      inlineFailureSection.indexOf('inlineCancelledCellRef.current = cellKey;') <
        inlineFailureSection.indexOf('setInlineEditingCell(null);'),
      '失败关闭编辑器前应标记当前单元格已取消，防止卸载失焦再次提交',
    )
    assert(
      inlineHandlers.includes("event.key === 'Enter'") &&
        inlineHandlers.includes("event.key === 'Escape'") &&
        inlineHandlers.includes('void handleInlineSave(cell);'),
      'Enter 应触发共享保存函数，Esc 应取消当前编辑',
    )

    const columnsSection = extractSection(
      pageSource,
      'const baseColumns = useMemo',
      'const draggableColumnKeys',
    )
    for (const field of ['minOrderQuantity', 'domesticPrice', 'importPrice', 'labelPrice']) {
      assert(
        columnsSection.includes(`renderInlineEditableNumberCell(record, '${field}')`),
        `${field} 列应复用双击编辑单元格`,
      )
    }
    assert(
      pageSource.includes('onDoubleClick={canInlineEditWarehouseProduct ? () => handleStartInlineEdit(record, field) : undefined}') &&
        pageSource.includes('onBlur={() => void handleInlineSave(cell)}') &&
        pageSource.includes('disabled={isSaving}') &&
        pageSource.includes('warehouse-products-inline-editor'),
      '单元格应双击进入编辑，失焦保存，保存中禁用输入且保持紧凑样式',
    )

    const openEditSection = extractSection(pageSource, 'const handleOpenEdit', 'const handleCloseModal')
    const modalSaveSection = extractSection(pageSource, 'const handleSave = async', 'const handleStartInlineEdit')
    assert(
      openEditSection.includes('middlePackQuantity: record.minOrderQuantity') &&
        modalSaveSection.includes('minOrderQuantity: values.middlePackQuantity') &&
        !modalSaveSection.includes('middlePackQuantity: values.middlePackQuantity'),
      '编辑弹窗应从列表 MinOrderQuantity 初始化，并仍以 MinOrderQuantity 保存',
    )
  })
  if (inlineEditFailure) failures.push(inlineEditFailure)

  const headerMenuLayoutFailure = await runTest('页头按钮应归入价格与同步、导入导出菜单，只保留一个主按钮', () => {
    // 2026-10 重设计：标题改为菜单名「仓库商品管理」；原「同步」「价格」菜单合并为「价格与同步」；
    // 「导入 / 导出」删掉两个只提示「暂缓到下一轮」的占位项；「管理分类」挪到左侧分类面板头部。
    const headerSection = extractSection(
      pageSource,
      "<PageContainer compact title={t('menu.warehouseProducts')}",
      '<div className="warehouse-products-layout">',
    )
    const priceAndSyncMenuSection = extractSection(
      headerSection,
      "<ToolbarMenuButton label={t('warehouseUi.products.priceAndSync')}",
      "<ToolbarMenuButton label={t('common.listToolbar.importExport', '导入 / 导出')}",
    )
    const importExportMenuSection = extractSection(
      headerSection,
      "<ToolbarMenuButton label={t('common.listToolbar.importExport', '导入 / 导出')}",
      '{access.canWriteProduct ? (<Button type="primary"',
    )
    assert(
      priceAndSyncMenuSection.includes("label: t('warehouse.retailPriceChanges.entry'),") &&
        priceAndSyncMenuSection.includes("onClick: () => navigate('/warehouse/products/retail-price-changes'),") &&
        priceAndSyncMenuSection.includes("label: t('warehouse.priceUpdateTasks.entry'),") &&
        priceAndSyncMenuSection.includes("onClick: () => navigate('/warehouse/products/price-update-tasks'),") &&
        (priceAndSyncMenuSection.match(/visible: access\.canManageWarehouseProducts,/g) ?? []).length === 2,
      '「价格与同步」菜单应包含零售价月度变化与价格变更任务，均按仓库商品管理权限显示',
    )
    assert(
      headerSection.includes("subtitle={t('warehouseUi.products.totalCount', { total: formatWarehouseProductCount(total) })}") &&
        !pageSource.includes("t('warehouse.productManagementSubtitle')"),
      '紧凑页头副标题应显示记录总数，不再显示原说明文字',
    )
    assert(
      !priceAndSyncMenuSection.includes("t('warehouse.hqSync', '从HQ同步库存')") &&
        priceAndSyncMenuSection.includes("t('warehouse.storePriceSync.title', '更新分店价格')") &&
        priceAndSyncMenuSection.includes('visible: canManageWarehouseStorePriceSync,') &&
        priceAndSyncMenuSection.includes('disabled: storePriceSyncOpen,') &&
        priceAndSyncMenuSection.includes('onClick: () => setStorePriceSyncOpen(true),'),
      '「价格与同步」菜单应包含按权限显示的更新分店价格（从HQ同步库存已停用）',
    )
    assert(
      importExportMenuSection.includes('icon={exporting ? <LoadingOutlined /> : undefined}') &&
        importExportMenuSection.includes("label: t('warehouse.exportExcel'),") &&
        importExportMenuSection.includes('disabled: exporting,') &&
        importExportMenuSection.includes('onClick: () => setExportConfigOpen(true),') &&
        importExportMenuSection.includes('onClick: () => setImportFromDomesticOpen(true),') &&
        importExportMenuSection.includes('visible: canImportNonHbProducts,') &&
        !pageSource.includes("t('warehouse.batchImageUploadMigrated')") &&
        !pageSource.includes("t('warehouse.batchSetMigrated')"),
      '「导入 / 导出」菜单应包含导出、国内导入、非国内导入（按权限），不再保留两个「暂缓」占位入口',
    )
    assert(
      headerSection.includes('{exporting ? (<Typography.Text type="secondary">') &&
        headerSection.includes('{exportMessage} ({exportProgress}%)'),
      '导出进度文字仍应显示在页头',
    )
    assert(
      !headerSection.includes('<Button icon={<HistoryOutlined />}') &&
        !headerSection.includes('setCategoryManageOpen') &&
        headerSection.includes('{access.canWriteProduct ? (<Button type="primary" icon={<PlusOutlined />} onClick={handleOpenCreate}>') &&
        countOccurrences(headerSection, 'type="primary"') === 1,
      '页头价格入口不再平铺、管理分类移到分类面板，新建商品为唯一主按钮',
    )
    for (const batchText of ["t('warehouse.batchActivate')", "t('warehouse.batchDeactivate')", "t('warehouse.batchEdit'", "t('warehouseUi.products.selectionSetCategory')", 'handlePushToHq']) {
      assert(!headerSection.includes(batchText), `页头不应再出现只对勾选行生效的操作：${batchText}`)
    }
  })
  if (headerMenuLayoutFailure) failures.push(headerMenuLayoutFailure)

  const selectionBarFailure = await runTest('批量操作应位于勾选后操作条，并保留确认、权限与禁用逻辑', () => {
    const selectionSection = extractSection(
      pageSource,
      '<SelectionActionBar selectedCount={selectedRowKeys.length} onClearSelection={() => setSelectedRowKeys([])}>',
      '</SelectionActionBar>',
    )
    assert(
      selectionSection.includes("<Popconfirm title={t('warehouse.confirmBatchActivate')}") &&
        selectionSection.includes('onConfirm={() => void handleBatchToggleActive(true)}') &&
        // 批量下架不再用 Popconfirm：供货说明弹窗（后续计划必选）本身就是确认步骤。
        !selectionSection.includes("t('warehouse.confirmBatchDeactivate')") &&
        selectionSection.includes('onClick={() => void handleBatchToggleActive(false)}') &&
        selectionSection.includes('disabled={!selectedRowKeys.length || batchActionLoading}') &&
        selectionSection.includes('onClick={openBatchEdit}') &&
        selectionSection.includes('onClick={openBatchCategory}') &&
        selectionSection.includes('disabled={!selectedRowKeys.length || batchCategorySaving}') &&
        selectionSection.includes('onClick={() => void handlePushToHq()}') &&
        countOccurrences(selectionSection, '{access.canWriteProduct ?') === 4 &&
        countOccurrences(selectionSection, '{access.canManagePosProducts ?') === 1,
      '勾选后操作条应包含批量修改、改分类、批量上架（带确认）、批量下架（经供货说明弹窗确认）和发送到HQ，且权限不变',
    )
    // 2026-10 重设计：按钮顺序按设计为 批量修改 → 改分类 → 上架 → 下架… → 发送到 HQ。
    assert(
      selectionSection.indexOf('onClick={openBatchEdit}') < selectionSection.indexOf('onClick={openBatchCategory}') &&
        selectionSection.indexOf('onClick={openBatchCategory}') < selectionSection.indexOf('void handleBatchToggleActive(true)') &&
        selectionSection.indexOf('void handleBatchToggleActive(true)') < selectionSection.indexOf('void handleBatchToggleActive(false)') &&
        selectionSection.indexOf('void handleBatchToggleActive(false)') < selectionSection.indexOf('void handlePushToHq()'),
      '勾选后操作条按钮顺序应与设计一致',
    )
    // 列表卡片由 antd Card 改为页面自带样式的 section，顺序：状态页签 → 筛选行 → 已生效筛选条 → 勾选后操作条 → 表格。
    const listSection = extractSection(pageSource, '<section className="warehouse-products-list"', '</section>')
    const statusTabsIndex = listSection.indexOf('<StatusTabs')
    const filterRowIndex = listSection.indexOf('<div className="list-toolbar-filter-row">')
    const activeBarIndex = listSection.indexOf('<ActiveFilterBar')
    const selectionIndex = listSection.indexOf('<SelectionActionBar')
    const tableIndex = listSection.indexOf('<DndContext')
    assert(
      statusTabsIndex >= 0 && statusTabsIndex < filterRowIndex && filterRowIndex < activeBarIndex && activeBarIndex < selectionIndex && selectionIndex < tableIndex,
      '顺序应为状态页签 → 筛选行 → 已生效筛选条 → 勾选后操作条 → 表格',
    )
  })
  if (selectionBarFailure) failures.push(selectionBarFailure)

  const instantFilterFailure = await runTest('下拉与状态页签选完即查，关键词防抖即时查询、回车立即提交', () => {
    const filterRowSection = extractSection(
      pageSource,
      '<div className="list-toolbar-filter-row">',
      '<ActiveFilterBar',
    )
    assert(
      filterRowSection.includes('void loadData({ page: 1, supplierCode: value, filters: nextFilters });') &&
        filterRowSection.includes('void loadData({ page: 1, productType: value, filters: nextFilters });') &&
        filterRowSection.includes('void loadData({ page: 1, ...buildCategoryQueryValue(nextCategoryFilterValue) });') &&
        filterRowSection.includes('void loadData({ page: 1, filters: nextFilters });'),
      '国内供应商、商品类型、分类、澳洲供应商变化后应用覆盖参数立即请求第 1 页',
    )
    // 2026-10 重设计：状态筛选由状态页签承担，与原状态下拉一样镜像进 columnFilters 并立即查第 1 页。
    const statusTabSection = extractSection(pageSource, 'const handleStatusTabChange = (key: WarehouseProductStatusTabKey) => {', 'const statusTabItems')
    assert(
      statusTabSection.includes("const nextFilters = setFilterValues(columnFilters, 'isActive', value === undefined ? undefined : [String(value)]);") &&
        statusTabSection.includes('void loadData({ page: 1, isActive: value, filters: nextFilters });') &&
        pageSource.includes('<StatusTabs items={statusTabItems} activeKey={statusTabKey} onChange={handleStatusTabChange}'),
      '状态页签切换后应同步 isActive 与列头状态筛选，并立即请求第 1 页',
    )
    // 设计里「类型」与国内供应商并列在主筛选行，低频的澳洲供应商收进「更多筛选」。
    const moreFiltersSection = extractSection(filterRowSection, '<MoreFiltersButton', '</MoreFiltersButton>')
    assert(
      moreFiltersSection.includes('activeCount={columnFilters.localSupplierCode?.length ? 1 : 0}') &&
        moreFiltersSection.includes("setFilterValues(columnFilters, 'localSupplierCode', values)") &&
        !moreFiltersSection.includes('<Select value={productType}') &&
        filterRowSection.indexOf('<Select value={productType}') < filterRowSection.indexOf('<MoreFiltersButton'),
      '商品类型应在主筛选行，澳洲供应商收进「更多筛选」，角标为弹层内生效条件数',
    )
    assert(
      filterRowSection.includes('onPressEnter={handleSubmitSearch}') &&
        filterRowSection.includes('onChange={(event) => handleSearchTextChange(event.target.value)}') &&
        !filterRowSection.includes("t('common.listToolbar.searchEnterHint', '回车查询')") &&
        !filterRowSection.includes('<Button type="primary" onClick={handleSubmitSearch}>') &&
        !filterRowSection.includes("t('common.query')") &&
        filterRowSection.includes('<span className="list-toolbar-filter-spacer"/>') &&
        filterRowSection.indexOf('<span className="list-toolbar-filter-spacer"/>') < filterRowSection.indexOf('<ColumnSettingsButton') &&
        !filterRowSection.includes("t('warehouse.categories.uncategorizedOption'"),
      '搜索框输入即查（防抖）、回车立即提交，不再有查询按钮；筛选行尾是列设置；未分类入口在分类面板',
    )
    const submitSection = extractSection(pageSource, 'const handleSubmitSearch = () => {', 'const handleSearchTextChange')
    assert(
      submitSection.includes('clearSearchDebounce();') &&
        submitSection.includes('setSubmittedSearchText(searchText);') &&
        submitSection.includes('void loadData({ page: 1, searchText });') &&
        pageSource.includes('searchText: submittedSearchText,'),
      '回车应取消未触发的防抖并立即提交；其余请求应使用已提交关键词，而非输入框里未提交的草稿',
    )
    const debounceSection = extractSection(pageSource, 'const handleSearchTextChange = (value: string) => {', 'const handleResetFilters')
    assert(
      pageSource.includes('const WAREHOUSE_PRODUCTS_SEARCH_DEBOUNCE_MS = 300;') &&
        debounceSection.includes('clearSearchDebounce();') &&
        debounceSection.includes('if (!isMountedRef.current) {') &&
        debounceSection.includes('setSubmittedSearchText(value);') &&
        debounceSection.includes('void loadDataRef.current?.({ page: 1, searchText: value });') &&
        debounceSection.includes('}, WAREHOUSE_PRODUCTS_SEARCH_DEBOUNCE_MS);'),
      '关键词应防抖 300ms 后经最新 loader 查询第 1 页，卸载后不再发请求',
    )
  })
  if (instantFilterFailure) failures.push(instantFilterFailure)

  const activeFilterBarFailure = await runTest('已生效筛选条应按实际查询条件展示并可单独移除', () => {
    const loadDataSection = extractSection(pageSource, 'const loadData = async (', 'useLayoutEffect(() => {')
    assert(
      extractSection(loadDataSection, 'onSuccess: (result) => {', 'onError: (error) => {').includes('setAppliedQuery(query);'),
      '已生效条件应取自最新成功返回的查询，而不是界面上尚未提交的 state',
    )
    // 2026-10 重设计：「只看未分类」开关由左侧分类面板的「未分类」入口替代，分类条件仍以标签显示、可移除。
    const activeBarSection = extractSection(pageSource, '<ActiveFilterBar', '<SelectionActionBar')
    assert(
      activeBarSection.includes('items={activeFilterChips.map((chip) => ({') &&
        activeBarSection.includes('onRemove: () => handleRemoveActiveFilter(chip.key),') &&
        activeBarSection.includes('onClearAll={handleResetFilters}') &&
        !activeBarSection.includes("t('warehouse.onlyUncategorized', '只看未分类')") &&
        !pageSource.includes('handleToggleUncategorizedOnly'),
      '已生效筛选条应接入标签移除与清空全部，「只看未分类」开关已由分类面板替代',
    )
    assert(
      pageSource.includes('const activeFilterChips = useMemo(() => buildActiveFilterChips({') &&
        pageSource.includes('query: appliedQuery,'),
      '标签应由纯函数 buildActiveFilterChips 根据 appliedQuery 生成',
    )
    const removeSection = extractSection(pageSource, 'const handleRemoveActiveFilter = (chipKey: string) => {', 'return (<>')
    assert(
      removeSection.includes('const overrides = buildActiveFilterRemovalOverrides(chipKey, columnFilters);') &&
        removeSection.includes('setColumnFilters(overrides.filters);') &&
        removeSection.includes('clearSearchDebounce();') &&
        removeSection.includes('void loadData(overrides);'),
      '移除单个标签应同步 columnFilters、取消未触发的关键词防抖，并立即按覆盖参数重查',
    )
  })
  if (activeFilterBarFailure) failures.push(activeFilterBarFailure)

  if (failures.length > 0) {
    throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  }

  console.log('WarehouseProducts.hqSync.logic.test: ok')
}

await main()
