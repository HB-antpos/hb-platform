import { readFileSync } from 'node:fs'
import path from 'node:path'

function assert(condition: boolean, message: string) {
  if (!condition) {
    throw new Error(message)
  }
}

const pageSource = readFileSync(path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrderImportPriceVariance/index.tsx'), 'utf8')
const routeSource = readFileSync(path.resolve(process.cwd(), 'src/router/routes.tsx'), 'utf8')
const zhLocale = JSON.parse(readFileSync(path.resolve(process.cwd(), 'src/i18n/locales/zh.json'), 'utf8'))
const enLocale = JSON.parse(readFileSync(path.resolve(process.cwd(), 'src/i18n/locales/en.json'), 'utf8'))

// 2026-10 重设计：商品图并入「商品」列，基准金额并入「原始 / 基准金额」列第二行，
// 体积、装箱数收进「列设置」（默认隐藏、仍可服务端排序），新增发货数量列。
assert(
  pageSource.includes('<ProductListImage src={row.productImage}') &&
	    pageSource.includes("dataIndex: 'domesticPrice'") &&
	    pageSource.includes("dataIndex: 'unitVolume'") &&
	    pageSource.includes("dataIndex: 'packingQuantity'") &&
	    pageSource.includes("optionalColumns.includes('unitVolume')") &&
	    pageSource.includes("optionalColumns.includes('packingQuantity')") &&
	    pageSource.includes("dataIndex: 'warehouseImportPrice'") &&
	    pageSource.includes("dataIndex: 'firstContainerImportPrice'") &&
    pageSource.includes("dataIndex: 'allocQuantityTotal'") &&
    pageSource.includes("dataIndex: 'originalImportAmountTotal'") &&
    pageSource.includes('formatAmount(row.baselineImportAmountTotal)') &&
    pageSource.includes("dataIndex: 'varianceAmountTotal'"),
	  '商品汇总主表必须包含商品图片、国内价格、当前仓库进货价格、首次进货价、发货数量和三项金额合计，体积与装箱数可在列设置中打开',
	)

const editablePriceBlockStart = pageSource.indexOf('const renderEditablePriceCell')
const editablePriceBlockEnd = pageSource.indexOf('const openBatchWarehouseImportPriceModal')
const editablePriceBlock = pageSource.slice(editablePriceBlockStart, editablePriceBlockEnd)

	assert(
	  pageSource.includes('updateStoreOrderImportPriceVarianceDomesticPrice') &&
	    pageSource.includes('updateStoreOrderImportPriceVarianceWarehouseImportPrice') &&
	    pageSource.includes('function parsePriceDraft') &&
	    pageSource.includes("type EditablePriceField = 'domesticPrice' | 'warehouseImportPrice'") &&
	    pageSource.includes('const priceInputRefs = useRef') &&
	    pageSource.includes('const savingPriceKeyRef = useRef') &&
	    pageSource.includes('savingPriceKeyRef.current === key') &&
	    pageSource.includes('inputMode="decimal"') &&
	    pageSource.includes('event.currentTarget.select()') &&
    pageSource.includes("event.key === 'ArrowUp'") &&
    pageSource.includes("event.key === 'ArrowDown'") &&
    pageSource.includes("event.key === 'Enter'") &&
    pageSource.includes("event.key === 'Escape'") &&
    editablePriceBlockStart >= 0 &&
    editablePriceBlockEnd > editablePriceBlockStart &&
    !editablePriceBlock.includes('<InputNumber') &&
    !pageSource.includes('type="number"'),
	  '国内价格和当前仓库进货价格列必须使用普通 Input 内联编辑，支持全选、方向键、回车保存、Esc 取消，且不能出现数字加减控件',
	)

const warehouseImportPriceColumnIndex = pageSource.indexOf("dataIndex: 'warehouseImportPrice'")
const firstContainerImportPriceColumnIndex = pageSource.indexOf("dataIndex: 'firstContainerImportPrice'")
assert(
  warehouseImportPriceColumnIndex >= 0 &&
    firstContainerImportPriceColumnIndex > warehouseImportPriceColumnIndex,
  '当前仓库进货价格列必须位于首次货柜价列前面',
)

assert(
  pageSource.includes('const [selectedRowKeys, setSelectedRowKeys] = useState<Key[]>([])') &&
    pageSource.includes('rowSelection={{') &&
    pageSource.includes('selectedRowKeys,') &&
    pageSource.includes('preserveSelectedRowKeys: true') &&
    pageSource.includes('getCheckboxProps: (row) => ({ disabled: !row.productCode })') &&
    pageSource.includes('openBatchWarehouseImportPriceModal') &&
    pageSource.includes('handleBatchWarehouseImportPriceSave') &&
    pageSource.includes('batchUpdateStoreOrderImportPriceVarianceWarehouseImportPrice({') &&
    pageSource.includes('productCodes,') &&
    pageSource.includes('warehouseImportPrice: values.warehouseImportPrice ?? 0') &&
    pageSource.includes('setSelectedRowKeys([])') &&
    pageSource.includes('await loadData()') &&
    // 2026-10 重设计统一叫法为「仓库进货价」，批量弹窗标题改用页面级文案键。
    pageSource.includes("title={t('warehouseUi.priceVariance.batchTitle'") &&
    pageSource.includes('<SelectionActionBar selectedCount={selectedRowKeys.length}') &&
    pageSource.includes('<InputNumber') &&
    pageSource.includes('批量修改只提交商品编码和统一的新当前参考进货价'),
  '商品汇总主表必须支持勾选商品后批量修改当前参考进货价，成功后清空选择并刷新统计结果',
)

// 2026-10 重设计：筛选改为工具栏即时查询（不再用 Form 字段），供应商显示在商品列第二行和供应商排行中。
assert(
  pageSource.includes('<DomesticSupplierFilterSelect') &&
    pageSource.includes('value={filterValues.supplierCode}') &&
    pageSource.includes("t('storeOrders.importPriceVariance.domesticSupplier')") &&
    pageSource.includes('row.supplierName, row.supplierCode'),
  '页面必须包含国内供应商筛选组件，并在商品行展示国内供应商',
)

assert(
  pageSource.includes("import { getActiveChinaSuppliers }") &&
    pageSource.includes('function DomesticSupplierFilterSelect') &&
    pageSource.includes('getActiveChinaSuppliers(currentController.signal)') &&
    pageSource.includes('onOpenChange={handleSupplierOpenChange}'),
  '国内供应商过滤组件必须复用 getActiveChinaSuppliers 并在首次展开时加载',
)

assert(
  pageSource.includes('const DEFAULT_PAGE_SIZE = 20') &&
    pageSource.includes("const DEFAULT_SORT_BY = 'absoluteVarianceAmount'") &&
    pageSource.includes('const DEFAULT_SORT_DESCENDING = true'),
  '页面默认分页和排序必须符合后端统计页契约',
)

assert(
  pageSource.includes("dataIndex: 'varianceAmountTotal'") &&
    pageSource.includes("key: 'varianceAmountTotal'") &&
    pageSource.includes("const DEFAULT_SORT_BY = 'absoluteVarianceAmount'"),
  '商品汇总差额合计列点击排序必须发送有符号 varianceAmountTotal，默认首屏才使用绝对差额排序',
)

assert(
  pageSource.includes('getStoreOrderImportPriceVariance(query)') &&
    pageSource.includes('onChange={handleTableChange}'),
  '主表必须通过服务端接口加载并响应表格分页排序',
)

// 2026-10 重设计：供应商卡取消一屏固定高度与内部滚动、改为默认显示前 6 个 +「展开全部」，
// 排序改在逻辑文件里本地完成（仍是全部供应商、可按列排序）；点击供应商行即设置国内供应商筛选、再点取消。
assert(
  pageSource.includes('const [supplierSummaries, setSupplierSummaries]') &&
    pageSource.includes('setSupplierSummaries(result.supplierSummaries)') &&
    pageSource.includes('const supplierSummaryColumns') &&
    pageSource.includes(
      '<MeasuredTable<StoreOrderImportPriceVarianceSupplierSummary> metricId="warehouse.store-order-import-price-variance.table-1"',
    ) &&
    pageSource.includes("t('warehouseUi.priceVariance.supplierTitle')") &&
    pageSource.includes('noSupplierVarianceData') &&
    pageSource.includes("dataIndex: 'increaseVarianceAmountTotal'") &&
    pageSource.includes("dataIndex: 'decreaseVarianceAmountTotal'") &&
    pageSource.includes('sortSupplierSummaries(supplierRows, supplierSort)') &&
    pageSource.includes('getVisibleSupplierRows(sortedSupplierRows, supplierExpanded, filters.supplierCode)') &&
    pageSource.includes('onChange={handleSupplierTableChange}') &&
    pageSource.includes('onRow={(row) => ({ onClick: () => toggleSupplierFilter(row) })}') &&
    pageSource.includes('filterValuesRef.current.supplierCode === row.supplierCode ? undefined : row.supplierCode') &&
    !pageSource.includes('result.supplierSummaries.slice(0, 10)') &&
    !pageSource.includes('SUPPLIER_SUMMARY_PLACEHOLDER_COUNT') &&
    !pageSource.includes('supplierSummaryTableScrollY'),
  '页面必须展示当前筛选下全部国内供应商的可排序差额统计，默认前 6 个可展开全部，并能点行联动商品筛选',
)

// 选中某个供应商后接口只返回该供应商汇总：排行必须复用同条件下的全量缓存，缓存不匹配时补一次不带供应商的请求。
assert(
  pageSource.includes('getSupplierRankingKey(filters)') &&
    pageSource.includes('supplierCode: undefined,') &&
    pageSource.includes('rankingGuard.isLatest(requestId)') &&
    pageSource.includes('supplierRanking?.key === supplierRankingKey ? supplierRanking.rows : supplierSummaries'),
  '点选供应商后排行仍需显示同条件下的全部供应商',
)

// 2026-10 重设计：取消主表与供应商卡的一屏固定高度和 body 内部滚动（多层滚动难用），整页自然滚动。
assert(
  !pageSource.includes('useLayoutEffect') &&
    !pageSource.includes('tableScrollY') &&
    !pageSource.includes("height: 'calc(100vh - 32px)'") &&
    pageSource.includes('scroll={{ x: PRODUCT_TABLE_BASE_WIDTH + optionalColumns.length * PRODUCT_OPTIONAL_COLUMN_WIDTH }}') &&
    pageSource.includes('scroll={{ x: 860 }}'),
  '主表与供应商卡不得再限制为一屏高度的内部滚动',
)

// 默认列（不含列设置里的可选列）在 1440 宽屏内放得下，差额数值不能被挤出视野。
const productTableBaseWidth = Number(pageSource.match(/const PRODUCT_TABLE_BASE_WIDTH = (\d+)/)?.[1])
assert(productTableBaseWidth > 0 && productTableBaseWidth <= 1100, `商品表默认宽度 ${productTableBaseWidth}px 超出 1440 宽屏可用宽度`)

// 筛选即查询：关键字等文本防抖 300ms，下拉 / 日期 / 分段立即生效；主表、排行、明细各自只采纳最后一次响应。
assert(
  pageSource.includes('const FILTER_DEBOUNCE_MS = 300') &&
    pageSource.includes("updateFilterValues({ keyword: event.target.value }, 'debounced')") &&
    pageSource.includes("updateFilterValues({ varianceDirection: value }, 'immediate')") &&
    pageSource.includes("updateFilterValues({ orderDateRange: value }, 'immediate')") &&
    pageSource.includes('<ActiveFilterBar') &&
    pageSource.includes('<MoreFiltersButton activeCount={moreFilterCount}>') &&
    pageSource.includes("updateFilterValues({ storeCode: event.target.value }, 'debounced')") &&
    pageSource.includes("updateFilterValues({ orderNo: event.target.value }, 'debounced')") &&
    pageSource.includes('if (!listGuard.isLatest(requestId)) {') &&
    pageSource.includes('if (!detailGuard.isLatest(requestId)) {') &&
    !pageSource.includes('htmlType="submit"'),
  '筛选必须即时查询（文本防抖），保留分店编码与订单号筛选，且请求有竞态守卫',
)

// 多收 / 少收改用橙 / 蓝，不能再用红 / 绿标签表示差额方向。
assert(
  !pageSource.includes("'red'") &&
    !pageSource.includes("'green'") &&
    !pageSource.includes('#cf1322') &&
    !pageSource.includes('#389e0d'),
  '差额方向配色必须改为多收橙、少收蓝',
)

assert(
  pageSource.includes('getStoreOrderImportPriceVarianceDetails({') &&
    pageSource.includes('productCode: selectedProduct.productCode') &&
    pageSource.includes('<Modal') &&
    pageSource.includes('onChange={handleDetailTableChange}'),
  '点击商品订单明细必须打开弹窗并通过 details 接口服务端分页加载',
)

assert(
  pageSource.includes('...filters') &&
    pageSource.includes('supplierCode: trimText(values.supplierCode)'),
  '主表筛选和弹窗明细必须共享当前筛选条件，包括国内供应商',
)

assert(
  pageSource.includes("import { useNavigate } from 'react-router-dom'") &&
    pageSource.includes('const navigate = useNavigate()'),
  '页面必须使用 useNavigate 打开订单和货柜明细页',
)

assert(
  pageSource.includes('navigate(`/warehouse/store-order/detail/${row.orderGUID}`, {') &&
    pageSource.includes('state: { orderNo: row.orderNo }'),
  '弹窗订单号列必须跳转到对应订货明细并传入订单号作为详情页初始标题',
)

assert(
  pageSource.includes('navigate(`/warehouse/container/detail/${row.firstContainerCode}`)'),
  '首次货柜编号列必须跳转到对应货柜明细页',
)

const routeStart = routeSource.indexOf("path: '/warehouse/store-order-import-price-variance'")
const routeEnd = routeSource.indexOf("path: '/warehouse/store-order/detail/:id'", routeStart)
const routeBlock = routeSource.slice(routeStart, routeEnd)

assert(routeStart >= 0 && routeEnd > routeStart, '路由必须注册首次货柜价差异统计页')
assert(routeBlock.includes("title: 'menu.storeOrderImportPriceVariance'"), '路由标题 key 必须符合菜单契约')
assert(routeBlock.includes("icon: 'BarChartOutlined'"), '路由图标应使用 BarChartOutlined')
assert(
  routeBlock.includes("accessKey: 'canManageStoreOrderImportPriceVariance'"),
  '路由权限必须收束到首柜价差异专用仓库管理员权限',
)

const fallbackStart = routeSource.indexOf('function buildWarehouseStaffMenus')
const fallbackEnd = routeSource.indexOf('export function buildMenus', fallbackStart)
const fallbackBlock = routeSource.slice(fallbackStart, fallbackEnd)

assert(
  fallbackBlock.includes("key: '/warehouse/store-orders'") &&
    !fallbackBlock.includes("key: '/warehouse/store-order-import-price-variance'"),
  '仓库员工 fallback 菜单只能保留分店订货列表，不能暴露首柜价差异统计页',
)

assert(
  zhLocale.menu.storeOrderImportPriceVariance === '首次货柜价差异统计' &&
    enLocale.menu.storeOrderImportPriceVariance === 'First Container Price Variance',
  '中英文菜单文案必须存在',
)

assert(
  zhLocale.storeOrders.importPriceVariance.originalImportAmount === '原始金额' &&
    zhLocale.storeOrders.importPriceVariance.baselineImportAmount === '基准金额' &&
    zhLocale.storeOrders.importPriceVariance.varianceAmount === '差额',
  '中文统计页核心明细列文案必须自然可读',
)

assert(
	  zhLocale.storeOrders.importPriceVariance.domesticSupplier === '国内供应商' &&
	    zhLocale.storeOrders.importPriceVariance.productImage === '商品图片' &&
	    zhLocale.storeOrders.importPriceVariance.domesticPrice === '国内价格' &&
	    zhLocale.storeOrders.importPriceVariance.warehouseImportPrice === '当前仓库进货价格' &&
	    enLocale.storeOrders.importPriceVariance.warehouseImportPrice === 'Current Warehouse Import Price' &&
	    zhLocale.storeOrders.importPriceVariance.unitVolume === '体积' &&
	    zhLocale.storeOrders.importPriceVariance.packingQuantity === '装箱数',
  '中文商品汇总列文案必须存在',
)

assert(
  zhLocale.storeOrders.importPriceVariance.batchWarehouseImportPrice === '批量修改当前参考进货价' &&
    zhLocale.storeOrders.importPriceVariance.batchWarehouseImportPriceTitle ===
      '批量修改当前参考进货价 ({{count}} 个商品)' &&
    zhLocale.storeOrders.importPriceVariance.batchSaveWarehouseImportPriceSuccess ===
      '已批量保存 {{count}} 个商品的仓库进货价格' &&
    enLocale.storeOrders.importPriceVariance.batchWarehouseImportPrice ===
      'Batch update reference import price' &&
    enLocale.storeOrders.importPriceVariance.batchWarehouseImportPriceTitle ===
      'Batch Update Reference Import Price ({{count}} products)' &&
    enLocale.storeOrders.importPriceVariance.batchSaveWarehouseImportPriceSuccess ===
      'Saved warehouse import price for {{count}} products',
  '批量修改当前参考进货价的中英文按钮、标题和成功文案必须存在',
)

assert(
  zhLocale.storeOrders.importPriceVariance.directionIncrease === '多收' &&
    zhLocale.storeOrders.importPriceVariance.directionDecrease === '少收' &&
    enLocale.storeOrders.importPriceVariance.directionIncrease === 'Overcharged' &&
    enLocale.storeOrders.importPriceVariance.directionDecrease === 'Undercharged',
  '差额方向文案必须表达订单进货价相对首次货柜价的多收/少收语义',
)

assert(
  zhLocale.storeOrders.importPriceVariance.supplierVarianceRankingTitle === '国内供应商差额统计' &&
    zhLocale.storeOrders.importPriceVariance.increaseVarianceAmountTotal === '多收合计' &&
    zhLocale.storeOrders.importPriceVariance.decreaseVarianceAmountTotal === '少收合计' &&
    zhLocale.storeOrders.importPriceVariance.productCount === '商品数' &&
    zhLocale.storeOrders.importPriceVariance.detailCount === '明细数' &&
    zhLocale.storeOrders.importPriceVariance.noSupplierVarianceData === '暂无供应商差额数据' &&
    zhLocale.storeOrders.importPriceVariance.totalSuppliers === '共 {{total}} 个供应商' &&
    enLocale.storeOrders.importPriceVariance.supplierVarianceRankingTitle === 'Domestic Supplier Variance' &&
    enLocale.storeOrders.importPriceVariance.increaseVarianceAmountTotal === 'Overcharged Total' &&
    enLocale.storeOrders.importPriceVariance.decreaseVarianceAmountTotal === 'Undercharged Total' &&
    enLocale.storeOrders.importPriceVariance.productCount === 'Products' &&
    enLocale.storeOrders.importPriceVariance.detailCount === 'Details' &&
    enLocale.storeOrders.importPriceVariance.noSupplierVarianceData === 'No supplier variance data' &&
    enLocale.storeOrders.importPriceVariance.totalSuppliers === '{{total}} suppliers',
  '中英文供应商差额统计表格文案必须存在',
)

console.log('storeOrderImportPriceVariance.logic.test: ok')
