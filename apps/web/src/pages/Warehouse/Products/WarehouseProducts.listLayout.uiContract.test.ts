import { readFileSync } from 'node:fs'
import path from 'node:path'

// 仓库商品管理 2026-10 重设计的页面契约：状态页签计数、行操作「编辑 + ⋯」、列设置、缺零售价/未绑定货位等派生显示。
// 计数、列显示等纯逻辑在 warehouseProductsListView.test.ts / columnOrder.test.ts / columnFilters.test.ts 里实测，
// 这里只核对页面把它们接对了，以及竞态守卫、权限、下架供货说明等既有保护没有在改版中丢失。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function extractSection(source: string, startText: string, endText: string) {
  const startIndex = source.indexOf(startText)
  assert(startIndex >= 0, `未找到代码片段：${startText}`)
  const endIndex = source.indexOf(endText, startIndex + startText.length)
  assert(endIndex >= 0, `未找到结束片段：${endText}`)
  return source.slice(startIndex, endIndex)
}

const read = (relativePath: string) => readFileSync(path.resolve(process.cwd(), relativePath), 'utf8')
const pageSource = read('src/pages/Warehouse/Products/index.tsx')
const zhMessages = JSON.parse(read('src/pages/Warehouse/Products/warehouseProductsMessages.zh.json'))
const enMessages = JSON.parse(read('src/pages/Warehouse/Products/warehouseProductsMessages.en.json'))

// 1. 本页新文案懒注册，不进入首屏全局语言包
assert(
  pageSource.includes("import warehouseProductsMessagesZh from './warehouseProductsMessages.zh.json';") &&
    pageSource.includes('registerPageMessages({ zh: warehouseProductsMessagesZh, en: warehouseProductsMessagesEn });'),
  '本页 warehouseUi.products 文案必须随页面代码块懒注册',
)
assert(zhMessages.warehouseUi.products.pushToHq === '发送到 HQ' && enMessages.warehouseUi.products.pushToHq === 'Send to HQ', '发送到 HQ 必须有中英文文案')

// 2. 状态页签计数：按降级规则并行发 pageSize=1 计数请求，独立守卫只采纳最新一轮，失败不显示计数
const statusCountSection = extractSection(pageSource, 'const loadStatusCounts = (', 'const loadData = async (')
assert(statusCountSection.includes('planWarehouseProductStatusCounts({'), '计数应先按条件键规划，翻页、排序不重复计数')
assert(
  statusCountSection.includes('runLatestGuardedRequest(statusCountGuardRef.current,') &&
    statusCountSection.includes('buildWarehouseProductStatusCountQuery(query, tab)') &&
    statusCountSection.includes('Promise.all('),
  '计数请求应并行发出，并经独立的最新请求守卫写入',
)
assert(
  extractSection(statusCountSection, 'onError: (error) => {', '});').includes('setStatusCounts(null);'),
  '计数失败时应清空计数（页签只显示文字），不能保留上一组条件的数量',
)
assert(
  pageSource.includes('const statusCountGuardRef = useRef(createLatestRequestGuard());') &&
    !pageSource.includes('runLatestGuardedRequest(listRequestGuardRef.current, () => Promise.all('),
  '计数请求必须使用独立守卫，不能占用列表请求守卫',
)
const loadDataSuccess = extractSection(pageSource, 'onSuccess: (result) => {\n                setData(result.items);', 'onError: (error) => {')
assert(loadDataSuccess.includes('loadStatusCounts(query, result.total);'), '只有最新的列表成功响应才触发计数，当前页签直接复用列表 total')
const refreshSection = extractSection(pageSource, 'const refreshCurrentList = useCallback', 'const stopBatchUpdateJobPolling')
assert(refreshSection.includes('statusCountsStaleRef.current = true;'), '写操作后的刷新必须重新计数')
const lifecycleSection = extractSection(pageSource, 'useLayoutEffect(() => {\n        isMountedRef.current = true', 'useEffect(() => {')
assert(
  lifecycleSection.includes('statusCountGuardRef.current.invalidate();') &&
    lifecycleSection.includes('window.clearTimeout(searchDebounceTimerRef.current);'),
  '卸载时计数请求必须失效，未触发的关键词防抖必须取消',
)
const toggleSingleSection = extractSection(pageSource, 'const handleToggleSingleActive = async', 'const handleSupplyNoticeSubmit')
assert(
  toggleSingleSection.includes('statusCountsStaleRef.current = true;') && toggleSingleSection.includes('loadStatusCounts(appliedQuery);'),
  '单行上下架只就地更新、不重查列表，必须单独刷新状态页签计数',
)
assert(
  pageSource.includes('count: statusCounts?.counts[key],') && pageSource.includes('<StatusTabs items={statusTabItems}'),
  '状态页签计数只来自计数结果，缺失时不传 count',
)

// 3. 行操作：编辑 + ⋯；下架仍经供货说明弹窗（handleToggleSingleActive 内拦截），权限与原按钮一致
const columnsSection = extractSection(pageSource, 'const baseColumns = useMemo', 'const draggableColumnKeys')
const actionColumn = extractSection(columnsSection, "key: 'action'", '], [')
assert(
  actionColumn.includes('{access.canWriteProduct ? (<Tooltip title={t(\'common.edit\')}>') &&
    actionColumn.includes('onClick={() => handleOpenEdit(record)}') &&
    actionColumn.includes('<Dropdown trigger={[\'click\']}') &&
    actionColumn.includes('handleRowAction(record, String(key))') &&
    actionColumn.includes("t('warehouseUi.products.rowMoreAria'"),
  '操作列应只保留编辑按钮与 ⋯ 菜单，并带无障碍名称',
)
assert(actionColumn.includes('width: 60') && actionColumn.includes("fixed: 'right'"), '操作列应收窄并固定在右侧')
const rowActionSection = extractSection(pageSource, 'const buildRowActionItems = ', 'const baseColumns = useMemo')
assert(
  rowActionSection.includes('if (access.canManageWarehouseProducts) {') &&
    rowActionSection.includes("key: 'changeHistory'") &&
    rowActionSection.includes('if (access.canManageWarehouseProducts && (access.canViewContainers || access.canViewProductSalesAnalysis)) {') &&
    rowActionSection.includes("key: 'records'") &&
    rowActionSection.includes('if (access.canWriteProduct) {') &&
    rowActionSection.includes("key: 'deactivate', disabled: toggling") &&
    rowActionSection.includes("key: 'activate', disabled: toggling"),
  '⋯ 菜单应包含修改记录、数据查询、套装/多码管理、上下架，并沿用原权限与进行中禁用',
)
assert(
  rowActionSection.includes("setChangeHistoryProduct(record);") &&
    rowActionSection.includes('navigate(`/warehouse/products/${encodeURIComponent(record.productCode)}/records`);') &&
    rowActionSection.includes("void handleToggleSingleActive(record, key === 'activate');"),
  '⋯ 菜单动作应分发到修改记录抽屉、数据查询页与单行上下架处理器',
)
assert(
  !columnsSection.includes('<Switch') && !columnsSection.includes('handleToggleSingleActive(record, nextChecked)'),
  '状态列不再提供行内上下架开关',
)
const singleToggleGate = extractSection(pageSource, 'const handleToggleSingleActive = async', 'try {')
assert(
  singleToggleGate.includes("if (!nextIsActive && !supplyNotice) {") &&
    singleToggleGate.includes("origin: 'rowToggle' }"),
  '⋯ 菜单里的下架仍必须先填写供货说明（行来源 rowToggle）',
)

// 4. 状态单元格：上架绿色 / 下架灰色胶囊；下架行显示供货计划摘要，可点开修改说明
const summarySection = extractSection(pageSource, 'const renderSupplyNoticeSummary = ', 'const buildRowActionItems = ')
assert(
  summarySection.includes('buildSupplyNoticeSummary(notice, {') &&
    summarySection.includes("summary.tone === 'overdue' ? ' is-overdue'") &&
    summarySection.includes('if (!access.canWriteProduct) {') &&
    summarySection.includes("setSupplyNoticeTarget({ mode: 'edit', productCodes: [record.productCode], initial: notice ?? null })"),
  '下架行应显示供货计划摘要（逾期标红），有写权限时点击修改说明',
)

// 5. 派生显示：缺零售价琥珀色「—」、货位未绑定、商品第二行
assert(
  pageSource.includes("const retailPriceMissing = field === 'labelPrice' && isWarehouseProductRetailPriceMissing(value);") &&
    pageSource.includes("t('warehouseUi.products.retailPriceMissing')"),
  '零售价缺失时应显示琥珀色「—」并保留双击补价',
)
assert(columnsSection.includes("t('warehouseUi.products.locationUnbound')"), '没有货位时应显示「未绑定」')
const productCellSection = extractSection(pageSource, 'const renderProductCell = ', 'const renderSupplyNoticeSummary = ')
assert(
  productCellSection.includes('buildWarehouseProductMetaParts(record, formatMiddlePack)') &&
    productCellSection.includes('getWarehouseProductCategoryTooltip(record, categoryLookup, i18n.language)') &&
    productCellSection.includes('copyTextToClipboard(record.itemNumber)'),
  '商品列第二行应显示条码 · 分类末级 · 中包，悬浮显示完整分类路径，并保留货号复制',
)

// 6. 列设置与表格
assert(
  pageSource.includes('<ColumnSettingsButton options={columnSettingsOptions} visibleKeys={visibleOptionalColumns} onChange={handleVisibleOptionalColumnsChange}') &&
    pageSource.includes('WAREHOUSE_PRODUCT_OPTIONAL_COLUMN_KEYS.map((key) => ({ key, label: columnMap.get(key)?.title as ReactNode }))'),
  '列设置应列出全部低频列并接入显示状态',
)
assert(
  pageSource.includes('<MeasuredTable metricId="warehouse.products.table-2" className="warehouse-products-table" rowKey="productCode" virtual'),
  '主表应继续使用 MeasuredTable、虚拟滚动与 productCode 行键',
)
assert(
  pageSource.includes("showTotal: (count: number) => t('warehouseUi.products.totalCount', { total: formatWarehouseProductCount(count) }),"),
  '分页区应显示筛选结果总数',
)
assert(!/import \{[^}]*\bCard\b[^}]*\} from 'antd'/.test(pageSource), '列表卡片改用页面样式的 section，不再引入 antd Card')

console.log('WarehouseProducts.listLayout.uiContract.test: ok')
