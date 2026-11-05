import { readFileSync } from 'node:fs'
import path from 'node:path'
import { matchesProductPickerSupplierOption } from './productPickerSupplierFilter'

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
const detailSource = readFileSync(detailFile, 'utf8')
const productPickerSource = detailSource.slice(
  detailSource.indexOf('function ProductPickerModal'),
  detailSource.indexOf('function BatchEditModal'),
)

async function main() {
  const failures: string[] = []

  const supplierFilterFailure = await runTest('商品弹窗应提供国内供应商筛选下拉', () => {
    assert(
      detailSource.includes("placeholder={t('storeOrders.detail.filterDomesticSupplier', '筛选国内供应商')}") &&
        detailSource.includes('showSearch') &&
        detailSource.includes('optionFilterProp="label"') &&
        detailSource.includes('allowClear'),
      '商品弹窗缺少带中文兜底的国内供应商筛选下拉',
    )
  })
  if (supplierFilterFailure) failures.push(supplierFilterFailure)

  const supplierShopNumberFilterFailure = await runTest('国内供应商下拉应按编码、名称和店铺号本地搜索', () => {
    const supplierOption = {
      label: '复活节头扣-6022 (022)',
      value: 'CN-FILTER',
      supplierCode: 'CN-FILTER',
      supplierName: '复活节头扣-6022',
      shopNumber: '022',
    }

    assert(matchesProductPickerSupplierOption(' cn-filter ', supplierOption), '供应商编码搜索应忽略前后空白和大小写')
    assert(matchesProductPickerSupplierOption('复活节', supplierOption), '供应商名称搜索应命中')
    assert(matchesProductPickerSupplierOption('022', supplierOption), '店铺号搜索应命中')
    assert(!matchesProductPickerSupplierOption('不存在', supplierOption), '无关关键字不应命中')
    assert(
      productPickerSource.includes('supplierCode: item.supplierCode') &&
        productPickerSource.includes('supplierName: item.supplierName') &&
        productPickerSource.includes('shopNumber: item.shopNumber') &&
        productPickerSource.includes('filterOption={(input, option) =>') &&
        productPickerSource.includes('matchesProductPickerSupplierOption(input'),
      '国内供应商下拉未把供应商编码、名称和店铺号都纳入本地搜索',
    )
  })
  if (supplierShopNumberFilterFailure) failures.push(supplierShopNumberFilterFailure)

  const queryFailure = await runTest('商品弹窗搜索应同时支持货号与商品名称', () => {
    assert(
      detailSource.includes('const trimmedKeyword = nextKeyword.trim()') &&
        detailSource.includes('itemNumber: trimmedKeyword || undefined') &&
        detailSource.includes('productName: trimmedKeyword || undefined') &&
        detailSource.includes('supplierCode: nextSupplierCode || undefined') &&
        detailSource.includes('excludeOrderGUID: orderGUID') &&
        !detailSource.includes('excludeExistingWarehouseProducts: true'),
      '商品弹窗搜索未同时传递货号/条码与商品名称查询，或缺少国内供应商/排除条件',
    )
  })
  if (queryFailure) failures.push(queryFailure)

  const supplierStateFailure = await runTest('商品弹窗关闭时应重置供应商筛选状态', () => {
    assert(
      detailSource.includes("const [supplierCode, setSupplierCode] = useState<string>()") &&
        detailSource.includes('setSupplierCode(undefined)') &&
        detailSource.includes('setSupplierOptions([])'),
      '商品弹窗关闭后未重置供应商筛选状态',
    )
  })
  if (supplierStateFailure) failures.push(supplierStateFailure)

  const supplierColumnFailure = await runTest('商品弹窗应显示供应商名称列', () => {
    assert(
      // 列始终保留：有「查看国内供应商名称」权限时叫「供应商名称」，没有时改叫「供应商代码」并显示代码兜底。
      detailSource.includes("? t('column.supplierName', '供应商名称')") &&
        detailSource.includes(": t('column.supplierCode', '供应商代码')") &&
        detailSource.includes("dataIndex: 'domesticSupplierName'") &&
        detailSource.includes("record.domesticSupplierCode || '--'"),
      '商品弹窗缺少国内供应商名称列或编码兜底',
    )
  })
  if (supplierColumnFailure) failures.push(supplierColumnFailure)

  const retailPriceColumnFailure = await runTest('商品弹窗应在默认进口价后显示只读零售价列', () => {
    const defaultImportPriceIndex = productPickerSource.indexOf("title: t('column.defaultImportPrice')")
    const retailPriceIndex = productPickerSource.indexOf("title: t('column.oemPrice')")
    const allocQuantityIndex = productPickerSource.indexOf("title: t('column.allocQuantity')")
    const retailPriceColumnSource = productPickerSource.slice(retailPriceIndex, allocQuantityIndex)

    assert(
      defaultImportPriceIndex >= 0 &&
        retailPriceIndex > defaultImportPriceIndex &&
        allocQuantityIndex > retailPriceIndex &&
        retailPriceColumnSource.includes("dataIndex: 'oemPrice'") &&
        retailPriceColumnSource.includes('width: 62') &&
        retailPriceColumnSource.includes('formatCurrencyAmount(value)') &&
        !retailPriceColumnSource.includes('<InputNumber'),
      '商品弹窗缺少位于默认进口价与发货数之间的只读零售价列，或列宽/格式不符合要求',
    )
  })
  if (retailPriceColumnFailure) failures.push(retailPriceColumnFailure)

  const paginationFailure = await runTest('商品弹窗默认分页应为 100 且只允许 50/100/500', () => {
    assert(
      detailSource.includes('const PRODUCT_PICKER_DEFAULT_PAGE_SIZE = 100') &&
        detailSource.includes("const PRODUCT_PICKER_PAGE_SIZE_OPTIONS = ['50', '100', '500']") &&
        productPickerSource.includes('useState(PRODUCT_PICKER_DEFAULT_PAGE_SIZE)') &&
        productPickerSource.includes('setPageSize(PRODUCT_PICKER_DEFAULT_PAGE_SIZE)') &&
        productPickerSource.includes('pageSizeOptions: PRODUCT_PICKER_PAGE_SIZE_OPTIONS'),
      '商品弹窗分页默认值或页容量选项不符合 100 / 50-100-500 要求',
    )
  })
  if (paginationFailure) failures.push(paginationFailure)

  const compactTableFailure = await runTest('商品弹窗表格应固定布局且不配置横向滚动', () => {
    assert(
      productPickerSource.includes('className="store-order-product-picker-table"') &&
        productPickerSource.includes('tableLayout="fixed"') &&
        productPickerSource.includes('scroll={{ y: 440 }}') &&
        !productPickerSource.includes('scroll={{ x:') &&
        productPickerSource.includes('className="store-order-product-picker-modal"'),
      '商品弹窗表格缺少固定布局/专用 class，或仍配置了横向 scroll.x',
    )
  })
  if (compactTableFailure) failures.push(compactTableFailure)

  const compactRendererFailure = await runTest('商品弹窗关键列应使用紧凑图片、复制图标、窄数字输入和价格 $ 前缀', () => {
    assert(
      productPickerSource.includes('width={32}') &&
        productPickerSource.includes('height={32}') &&
        productPickerSource.includes('icon={<CopyOutlined />}') &&
        productPickerSource.includes('className="store-order-picker-copy-button"') &&
        productPickerSource.includes('className="store-order-picker-two-line"') &&
        productPickerSource.includes('className="store-order-picker-number-input"') &&
        productPickerSource.includes('className="store-order-picker-number-input store-order-picker-price-input"') &&
        productPickerSource.includes('prefix="$"') &&
        productPickerSource.includes('formatCurrencyAmount(value)') &&
        productPickerSource.includes('style={{ width: 58 }}') &&
        productPickerSource.includes('style={{ width: 70 }}'),
      '商品弹窗未压缩图片、复制按钮、文本列、数字输入框或价格 $ 前缀',
    )
  })
  if (compactRendererFailure) failures.push(compactRendererFailure)

  const abortFailure = await runTest('商品弹窗商品请求应支持取消旧请求', () => {
    assert(
      productPickerSource.includes('const productRequestControllerRef = useRef<AbortController | null>(null)') &&
        productPickerSource.includes('productRequestControllerRef.current?.abort()') &&
        productPickerSource.includes('const currentController = new AbortController()') &&
        productPickerSource.includes('currentController.signal') &&
        productPickerSource.includes('if (isAbortError(error))') &&
        productPickerSource.includes('productRequestControllerRef.current !== currentController'),
      '商品弹窗商品请求缺少 AbortController 取消或旧请求防回写',
    )
  })
  if (abortFailure) failures.push(abortFailure)

  const supplierLazyFailure = await runTest('国内供应商下拉应首次展开才异步加载并可关闭取消', () => {
    assert(
      productPickerSource.includes('const supplierRequestControllerRef = useRef<AbortController | null>(null)') &&
        productPickerSource.includes('const supplierOptionsLoadedRef = useRef(false)') &&
        productPickerSource.includes('getActiveChinaSuppliers(currentController.signal)') &&
        productPickerSource.includes('onOpenChange={(visible) =>') &&
        productPickerSource.includes('void loadSupplierOptions()') &&
        productPickerSource.includes('return') &&
        productPickerSource.includes('supplierRequestControllerRef.current?.abort()') &&
        productPickerSource.includes('supplierRequestControllerRef.current = null') &&
        productPickerSource.includes('setSupplierLoading(false)') &&
        !productPickerSource.includes('void loadSupplierOptions()\n    void loadProducts'),
      '国内供应商下拉未按首次展开懒加载，或关闭时不能取消未完成请求',
    )
  })
  if (supplierLazyFailure) failures.push(supplierLazyFailure)

  const columnFilterFailure = await runTest('商品弹窗表头应提供服务端列过滤和排序状态', () => {
    assert(
      productPickerSource.includes("const [productSortBy, setProductSortBy] = useState('Default')") &&
        productPickerSource.includes('const [productSortDescending, setProductSortDescending] = useState(false)') &&
        productPickerSource.includes('const [columnFilters, setColumnFilters] = useState<StoreOrderProductColumnFilters>({})') &&
        productPickerSource.includes('cleanProductPickerColumnFilters(nextColumnFilters)') &&
        productPickerSource.includes('columnFilters: cleanedColumnFilters') &&
        productPickerSource.includes("productTextFilterProps('itemNumber'") &&
        productPickerSource.includes("productTextFilterProps('productName'") &&
        productPickerSource.includes("productTextFilterProps('supplierKeyword'") &&
        productPickerSource.includes("productTextFilterProps('barcode'") &&
        productPickerSource.includes("productNumberFilterProps({ min: 'stockQuantityMin', max: 'stockQuantityMax' })") &&
        productPickerSource.includes("productNumberFilterProps({ min: 'minOrderQuantityMin', max: 'minOrderQuantityMax' })") &&
        productPickerSource.includes("productNumberFilterProps({ min: 'importPriceMin', max: 'importPriceMax' })") &&
        productPickerSource.includes("sortOrder: productSortOrder('importPrice')"),
      '商品弹窗缺少列过滤/排序状态、请求参数或核心列表头过滤配置',
    )
  })
  if (columnFilterFailure) failures.push(columnFilterFailure)

  const columnFilterDraftIsolationFailure = await runTest('商品弹窗表头未应用草稿不应跨列提交', () => {
    assert(
      detailSource.includes('function ProductPickerTextFilterDropdown') &&
        detailSource.includes('const [draft, setDraft] = useState(value ??') &&
        productPickerSource.includes('applyProductColumnFilterPatch({ [key]: value }, nextConfirm)') &&
        productPickerSource.includes('每个表头弹层只提交自己的 patch') &&
        !detailSource.includes('columnFilterDrafts'),
      '商品弹窗仍可能把其他列未应用的筛选草稿一起提交',
    )
  })
  if (columnFilterDraftIsolationFailure) failures.push(columnFilterDraftIsolationFailure)

  const preservedSelectionFailure = await runTest('商品弹窗跨页选择应缓存已选商品实体', () => {
    assert(
      productPickerSource.includes('const [selectedProductsByCode, setSelectedProductsByCode] = useState<Record<string, StoreOrderProductItem>>({})') &&
        productPickerSource.includes('preserveSelectedRowKeys: true') &&
        productPickerSource.includes('nextSelectedRows.forEach((product) =>') &&
        productPickerSource.includes('selectedProductsByCode[String(key)] ?? products.find') &&
        productPickerSource.includes('setSelectedProductsByCode({})'),
      '商品弹窗跨页/过滤后选中商品没有实体缓存，确认添加可能丢失非当前页商品',
    )
  })
  if (preservedSelectionFailure) failures.push(preservedSelectionFailure)

  const tableChangeFailure = await runTest('商品弹窗分页排序应统一走表格 onChange 并忽略本地 filter action', () => {
    assert(
      productPickerSource.includes("if (extra.action === 'filter')") &&
        productPickerSource.includes("extra.action === 'sort' ? 1 : pagination.current || 1") &&
        productPickerSource.includes("nextSortBy = 'Default'") &&
        productPickerSource.includes('sortDescending: nextSortDescending') &&
        !productPickerSource.includes('onChange: (nextPage, nextPageSize) =>\n              void loadProducts'),
      '商品弹窗分页/排序没有统一使用 Table onChange，或未避免 filter action 重复触发',
    )
  })
  if (tableChangeFailure) failures.push(tableChangeFailure)

  const quickAddFailure = await runTest('快速添加请求仍保持原始查询结构', () => {
    assert(
      detailSource.includes('const result = await getStoreOrderProducts({') &&
        detailSource.includes('itemNumber: normalizedItemNumber') &&
        detailSource.includes('includeInactiveWarehouseProducts: true') &&
        detailSource.includes('pageNumber: 1') &&
        detailSource.includes('pageSize: 50') &&
        detailSource.includes("sortBy: 'Default'"),
      '快速添加商品查询结构被意外改动',
    )
  })
  if (quickAddFailure) failures.push(quickAddFailure)

  if (failures.length > 0) {
    throw new Error(`共有 ${failures.length} 个测试失败\\n- ${failures.join('\\n- ')}`)
  }

  console.log('productPickerModal.logic.test: ok')
}

await main()
