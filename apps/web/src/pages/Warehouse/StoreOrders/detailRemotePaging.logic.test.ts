import { readFileSync } from 'node:fs'
import path from 'node:path'

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

async function main() {
  const failures: string[] = []

  const loadStateFailure = await runTest('详情页应显式区分 idle/loading/loaded/notFound/error 状态', () => {
    assert(
      detailSource.includes("type DetailLoadStatus = 'idle' | 'loading' | 'loaded' | 'notFound' | 'error'"),
      '详情页尚未声明远程加载状态机',
    )
  })
  if (loadStateFailure) failures.push(loadStateFailure)

  const remoteQueryFailure = await runTest('详情页应将分页筛选排序作为远程 query 发送', () => {
    assert(
      detailSource.includes('pageNumber: detailPage') &&
        detailSource.includes('pageSize: detailPageSize') &&
        detailSource.includes('keyword: detailItemFilter.trim() || undefined') &&
        detailSource.includes("statFilter: detailStatFilter === 'all' ? undefined : detailStatFilter") &&
        detailSource.includes('columnFilters: cleanedDetailColumnFilters') &&
        detailSource.includes('sortBy: detailSortField || undefined') &&
        detailSource.includes("sortDescending: detailSortField ? detailSortOrder === 'descend' : undefined"),
      '详情页尚未把分页筛选排序拼到远程明细查询里',
    )
  })
  if (remoteQueryFailure) failures.push(remoteQueryFailure)

  // 重设计：原「默认排序」按钮改为工具栏「排序」下拉，选「按货位（默认）」即恢复默认排序（仍走 handleResetDetailDefaultSort）。
  const defaultLocationSortFailure = await runTest('详情页默认排序应按货位升序并提供恢复默认排序入口', () => {
    const sortChangeSource = detailSource.slice(
      detailSource.indexOf('const handleChangeDetailSortField = (field: StoreOrderDetailSortField) => {'),
      detailSource.indexOf('const handleToggleDetailSortOrder = () => {'),
    )
    assert(
      detailSource.includes("useState<DetailSortField>('locationCode')") &&
        detailSource.includes("useState<SortOrder>('ascend')") &&
        detailSource.includes('const handleResetDetailDefaultSort = () =>') &&
        detailSource.includes("setDetailSortField('locationCode')") &&
        detailSource.includes("setDetailSortOrder('ascend')") &&
        sortChangeSource.includes("if (field === 'locationCode') {") &&
        sortChangeSource.includes('handleResetDetailDefaultSort()') &&
        sortChangeSource.includes('setSelectedLineKeys([])') &&
        sortChangeSource.includes('setDetailPage(1)') &&
        detailSource.includes("const STORE_ORDER_DETAIL_SORT_OPTION_FIELDS: StoreOrderDetailSortField[] = [\n  'locationCode',") &&
        detailSource.includes("locationCode: t('warehouseUi.storeOrderDetail.sortLocation')") &&
        detailSource.includes('onChange={handleChangeDetailSortField}'),
      '详情页尚未默认按货位升序，或排序下拉缺少「按货位（默认）」恢复入口',
    )
  })
  if (defaultLocationSortFailure) failures.push(defaultLocationSortFailure)

  const detailColumnFilterFailure = await runTest('详情页主明细关键列应支持列头过滤和服务端排序字段', () => {
    const requiredSortFields = [
      "'itemNumber'",
      "'productName'",
      "'barcode'",
      "'locationCode'",
      "'quantity'",
      "'allocQuantity'",
      "'importPrice'",
      "'isActive'",
    ]
    assert(
      detailSource.includes('useState<StoreOrderDetailColumnFilters>({})') &&
        detailSource.includes('cleanStoreOrderDetailColumnFilters(detailColumnFilters)') &&
        requiredSortFields.every((field) => detailSource.includes(field)) &&
        // 重设计：货号/名称/条码合并成「商品」列，列头放大镜里同时给出三个过滤框，仍分别提交原来的三个列筛选键。
        detailSource.includes('...detailProductFilterProps(),') &&
        detailSource.includes('itemNumber: detailColumnFilters.itemNumber,') &&
        detailSource.includes('productName: detailColumnFilters.productName,') &&
        detailSource.includes('barcode: detailColumnFilters.barcode,') &&
        detailSource.includes("clearDetailColumnFilter(['itemNumber', 'productName', 'barcode'], nextConfirm)") &&
        detailSource.includes("detailTextFilterProps('locationCode'") &&
        detailSource.includes("detailNumberFilterProps({ min: 'quantityMin', max: 'quantityMax' })") &&
        detailSource.includes("detailNumberFilterProps({ min: 'allocQuantityMin', max: 'allocQuantityMax' })") &&
        detailSource.includes("detailNumberFilterProps({ min: 'importPriceMin', max: 'importPriceMax' })") &&
        detailSource.includes('detailStatusFilterProps()') &&
        detailSource.includes('isStoreOrderDetailSortField(field)') &&
        detailSource.includes('applyDetailColumnFilters') &&
        detailSource.includes('setSelectedLineKeys([])') &&
        detailSource.includes('setDetailPage(1)'),
      '详情页主明细关键列尚未完整接入列头过滤、排序白名单和结果集切换保护',
    )
  })
  if (detailColumnFilterFailure) failures.push(detailColumnFilterFailure)

  const defaultPageSizeFailure = await runTest('详情页主明细默认每页 200 并只提供指定分页选项', () => {
    assert(
      detailSource.includes('const STORE_ORDER_DETAIL_DEFAULT_PAGE_SIZE = 200') &&
        detailSource.includes("const STORE_ORDER_DETAIL_PAGE_SIZE_OPTIONS = ['50', '100', '200', '500', '1000']") &&
        detailSource.includes('useState(STORE_ORDER_DETAIL_DEFAULT_PAGE_SIZE)') &&
        detailSource.includes('pageSizeOptions: STORE_ORDER_DETAIL_PAGE_SIZE_OPTIONS') &&
        !detailSource.includes("pageSizeOptions: ['20', '50', '100', '500']"),
      '详情页主明细默认分页或分页选项不符合 200 / 50-1000 要求',
    )
  })
  if (defaultPageSizeFailure) failures.push(defaultPageSizeFailure)

  const lazyImageFailure = await runTest('详情页主明细图片应使用浏览器原生懒加载', () => {
    assert(
      detailSource.includes('loading="lazy"') &&
        detailSource.includes('fallback="data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs="'),
      '详情页主明细图片列尚未设置 loading="lazy"',
    )
  })
  if (lazyImageFailure) failures.push(lazyImageFailure)

  const currentPageDataFailure = await runTest('详情表格应直接使用服务端当前页 items 与 itemsTotal', () => {
    assert(
      detailSource.includes('dataSource={detail.items}') &&
        detailSource.includes('total: detail.itemsTotal ?? detail.items.length') &&
        !detailSource.includes('dataSource={pagedItems}'),
      '详情表格仍在使用本地切片分页，而不是服务端当前页数据',
    )
  })
  if (currentPageDataFailure) failures.push(currentPageDataFailure)

  const clearSelectionFailure = await runTest('翻页筛选排序时应清空勾选行', () => {
    // 重设计：明细搜索改为防抖约 300ms 后才写入 detailItemFilter（与仓库各列表统一），写入时仍先清空勾选并回到第一页。
    const keywordDebounceSource = detailSource.slice(
      detailSource.indexOf('if (detailKeywordInput.trim() === detailItemFilter.trim()) {'),
      detailSource.indexOf('}, [detailItemFilter, detailKeywordInput])'),
    )
    const statTabSource = detailSource.slice(
      detailSource.indexOf('const handleChangeDetailStatFilter = (nextFilter: StoreOrderDetailStatFilter) => {'),
      detailSource.indexOf('const focusDetailStatFilter'),
    )
    assert(
      detailSource.includes('setSelectedLineKeys([])') &&
        detailSource.includes('setDetailPage(nextPage)') &&
        detailSource.includes("extra.action === 'paginate'") &&
        detailSource.includes("extra.action === 'filter'") &&
        detailSource.includes('onChange={(event) => setDetailKeywordInput(event.target.value)}') &&
        detailSource.includes('const STORE_ORDER_DETAIL_KEYWORD_DEBOUNCE_MS = 300') &&
        keywordDebounceSource.includes('window.setTimeout(') &&
        keywordDebounceSource.includes('setSelectedLineKeys([])') &&
        keywordDebounceSource.includes('setDetailPage(1)') &&
        keywordDebounceSource.includes('setDetailItemFilter(detailKeywordInput)') &&
        keywordDebounceSource.includes('window.clearTimeout(timer)') &&
        statTabSource.includes('setSelectedLineKeys([])') &&
        statTabSource.includes('setDetailPage(1)') &&
        detailSource.includes('setDetailSortField(field)'),
      '翻页、搜索、页签、排序时尚未统一清空 selectedLineKeys',
    )
  })
  if (clearSelectionFailure) failures.push(clearSelectionFailure)

  const cancelFailure = await runTest('详情页应取消上一笔进行中的明细请求', () => {
    assert(
      detailSource.includes('detailRequestControllerRef.current?.abort()') &&
        detailSource.includes('new AbortController()') &&
        detailSource.includes('detailRequestControllerRef.current.signal'),
      '详情页尚未接入明细请求取消逻辑',
    )
  })
  if (cancelFailure) failures.push(cancelFailure)

  const containerCodesFailure = await runTest('货柜选品应使用跨页商品编码去重', () => {
    assert(
      detailSource.includes('getStoreOrderDetailProductCodes') &&
        detailSource.includes('alreadySelectedCodes={containerExistingProductCodes}') &&
        detailSource.includes('handleOpenContainerPicker') &&
        detailSource.includes('setContainerPickerOpen(false)') &&
        !detailSource.includes('alreadySelectedCodes={detail.items.map((item) => item.productCode)}') &&
        !detailSource.includes('detail.items.map((item) => item.productCode)'),
      '货柜选品仍在使用当前页 items 做已选商品去重',
    )
  })
  if (containerCodesFailure) failures.push(containerCodesFailure)

  if (failures.length > 0) {
    throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  }

  console.log('detailRemotePaging.logic.test: ok')
}

await main()
