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

const storeOrdersFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/index.tsx')
const detailFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/Detail.tsx')
const appFile = path.resolve(process.cwd(), 'src/App.tsx')
const compactCssFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/compact.css')
const pickingListFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/PickingList.tsx')
const invoiceFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/Invoice.tsx')
const containerProductPickerFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/components/ContainerProductPicker.tsx')
const printCssFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/print.css')
const packageFile = path.resolve(process.cwd(), 'package.json')
const listLogicFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/storeOrderListLogic.ts')
const listMessagesZhFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/storeOrdersMessages.zh.json')
const detailCssFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/storeOrderDetail.css')
const detailMessagesZhFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/storeOrderDetailMessages.zh.json')

function readSource(file: string) {
  // 统一换行，避免 Windows CRLF 让源码契约断言误判。
  return readFileSync(file, 'utf8').replace(/\r\n/g, '\n')
}

const storeOrdersSource = readSource(storeOrdersFile)
const detailSource = readSource(detailFile)
const appSource = readSource(appFile)
const compactCssSource = readSource(compactCssFile)
const pickingListSource = readSource(pickingListFile)
const invoiceSource = readSource(invoiceFile)
const containerProductPickerSource = readSource(containerProductPickerFile)
const printCssSource = readSource(printCssFile)
const packageSource = readSource(packageFile)
const listLogicSource = readSource(listLogicFile)
const listMessagesZhSource = readSource(listMessagesZhFile)
const detailCssSource = readSource(detailCssFile)
const detailMessagesZhSource = readSource(detailMessagesZhFile)
const detailMainTableSource = detailSource.slice(detailSource.indexOf('const baseDetailColumns: ColumnsType<StoreOrderDetailLine>'))
const detailKeyboardHandlerSource = detailSource.slice(
  detailSource.indexOf('const handleDetailInputKeyDown'),
  detailSource.indexOf('const handleCompleteOrder'),
)
// 重设计：订单头 Descriptions 改为「订单信息」卡的字段布局，订货/出库日期位于分店与联系邮箱之间。
const detailHeaderDateSource = detailSource.slice(
  detailSource.indexOf("htmlFor={fieldId('orderDate')}"),
  detailSource.indexOf("htmlFor={fieldId('contactEmail')}"),
)

function readCssRule(source: string, selector: string) {
  const escapedSelector = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const match = source.match(new RegExp(`${escapedSelector}\\s*\\{([\\s\\S]*?)\\}`))
  return match?.[1] ?? ''
}

function readColumnBlock(source: string, dataIndex: string) {
  const dataIndexPosition = source.indexOf(`dataIndex: '${dataIndex}'`)
  if (dataIndexPosition < 0) {
    return ''
  }
  const blockStart = source.lastIndexOf('    {', dataIndexPosition)
  const nextBlockStart = source.indexOf('    {', dataIndexPosition + dataIndex.length)
  return source.slice(blockStart, nextBlockStart > 0 ? nextBlockStart : source.length)
}

function readNumericValue(source: string, pattern: RegExp) {
  const match = source.match(pattern)
  return match ? Number(match[1]) : Number.NaN
}

async function main() {
  const failures: string[] = []

  const detailClassFailure = await runTest('详情页主明细表应挂载紧凑样式 class', () => {
    assert(detailSource.includes("import './compact.css'"), '详情页应引入 StoreOrders 局部紧凑样式')
    assert(detailSource.includes('className="store-order-detail-table"'), '详情页主明细表缺少 store-order-detail-table class')
    // 重设计：原「统计过滤」条改为明细页签 + 工具栏，样式在页面级 storeOrderDetail.css。
    assert(detailSource.includes("import './storeOrderDetail.css'"), '详情页应引入页面级重设计样式')
    assert(detailSource.includes('className="wh-order-detail-toolbar"'), '详情页明细工具栏缺少样式 class')
    assert(detailSource.includes('renderStoreOrderDetailNumericCell('), '详情页数字列应走单行等宽数字 helper')
  })
  if (detailClassFailure) failures.push(detailClassFailure)

  const datePickerLocaleFailure = await runTest('详情页日期选择器应跟随站内中英文切换', () => {
    const detailDatePickerCount = detailHeaderDateSource.match(/<DatePicker/g)?.length ?? 0

    assert(appSource.includes("import enUS from 'antd/locale/en_US'"), '应用根层应引入 AntD 英文 locale')
    assert(appSource.includes("import zhCN from 'antd/locale/zh_CN'"), '应用根层应引入 AntD 中文 locale')
    assert(appSource.includes("import 'dayjs/locale/zh-cn'"), '应用根层应加载 dayjs 中文日期数据')
    assert(appSource.includes('const { i18n } = useTranslation()'), '应用根组件应订阅当前 i18n 语言')
    assert(appSource.includes("const antdLocale = i18n.resolvedLanguage === 'en' ? enUS : zhCN"), '应用根层应按当前语言选择 AntD locale')
    assert(appSource.includes('locale={antdLocale}'), '根 ConfigProvider 应接收当前 AntD locale')
    assert(detailSource.includes("import dayjs from 'dayjs'"), '详情页应使用 dayjs 为 AntD DatePicker 提供日期值')
    assert(detailDatePickerCount === 2, '订货日期和出库日期都应使用 AntD DatePicker')
    assert(!detailHeaderDateSource.includes('type="date"'), '详情页不应继续使用跟随浏览器语言的原生 date input')
    assert(detailHeaderDateSource.includes("orderDate: value ? new Date(value.format('YYYY-MM-DD')).toISOString() : undefined"), '订货日期切换控件后应保持原有 UTC 日期提交格式')
    assert(detailHeaderDateSource.includes("outboundDate: value?.format('YYYY-MM-DD')"), '出库日期切换控件后应保持 YYYY-MM-DD 提交格式')
  })
  if (datePickerLocaleFailure) failures.push(datePickerLocaleFailure)

  const listOrderNoFailure = await runTest('列表页订单号复制按钮应限制在订单号列内', () => {
    const orderCellRule = readCssRule(compactCssSource, '.store-order-list-table .store-order-list-order-cell')
    const orderButtonRule = readCssRule(compactCssSource, '.store-order-list-table .store-order-list-order-no')
    const copyButtonRule = readCssRule(compactCssSource, '.store-order-list-table .store-order-copy-button')

    assert(storeOrdersSource.includes('className="store-order-list-order-cell"'), '订单号列应挂载专属布局 class')
    assert(/width:\s*100%/.test(orderCellRule), '订单号布局容器应占满单元格宽度')
    assert(/min-width:\s*0/.test(orderCellRule), '订单号布局容器应允许内容收缩')
    assert(/flex:\s*0\s+0\s+auto/.test(orderButtonRule), '订单号文本应完整显示，不应被压缩省略')
    assert(!/text-overflow:\s*ellipsis/.test(orderButtonRule), '订单号文本不应省略显示')
    assert(!/overflow:\s*hidden/.test(orderButtonRule), '订单号文本不应被隐藏截断')
    assert(/flex:\s*0\s+0\s+20px/.test(copyButtonRule), '复制按钮应固定宽度，避免被挤出列')
  })
  if (listOrderNoFailure) failures.push(listOrderNoFailure)

  const listTwoLineFailure = await runTest('列表页分店和备注应最多显示两行', () => {
    // 改版：分店列为「名称一行 + 编码一行」的中性色文本，名称单行省略；不再用按编码哈希上色的 Tag。
    const storeNameRule = readCssRule(compactCssSource, '.store-order-list-table .wh-orders-store-name')
    const twoLineRule = readCssRule(compactCssSource, '.store-order-list-table .store-order-two-line-text')

    assert(storeOrdersSource.includes('className="wh-orders-store-name"'), '分店列应挂载名称行 class')
    assert(!storeOrdersSource.includes('getStoreColor('), '分店不应再按编码哈希上色')
    assert(storeOrdersSource.includes('renderStoreOrderTwoLineText(value)'), '备注列应使用两行文本 helper')
    assert(/text-overflow:\s*ellipsis/.test(storeNameRule), '分店名称超长应省略')
    assert(/overflow:\s*hidden/.test(storeNameRule), '分店名称超长应隐藏')
    assert(/white-space:\s*nowrap/.test(storeNameRule), '分店名称应单行显示，编码在第二行')
    assert(/-webkit-line-clamp:\s*2/.test(twoLineRule), '备注应最多显示两行')
    assert(/overflow:\s*hidden/.test(twoLineRule), '备注超过两行应隐藏')
    assert(/white-space:\s*normal/.test(twoLineRule), '备注应允许换行')
  })
  if (listTwoLineFailure) failures.push(listTwoLineFailure)

  const listColumnDragFailure = await runTest('列表页主表应支持和货柜明细一致的表头列拖拽', () => {
    assert(
      storeOrdersSource.includes('DndContext') &&
        storeOrdersSource.includes('SortableContext') &&
        storeOrdersSource.includes('useSortable') &&
        storeOrdersSource.includes('horizontalListSortingStrategy'),
      '列表页主表应复用 @dnd-kit 横向排序能力',
    )
    assert(
      // 改版合并了日期/数量/金额列，列序与列宽 key 升到 v2，旧布局不再套到新列上。
      storeOrdersSource.includes("const STORE_ORDER_LIST_COLUMN_ORDER_STORAGE_KEY = 'hbweb_rv.storeOrders.list.columnOrder.v2'") &&
        storeOrdersSource.includes('localStorage.setItem(STORE_ORDER_LIST_COLUMN_ORDER_STORAGE_KEY') &&
        storeOrdersSource.includes('mergeStoreOrderListColumnOrder('),
      '列表页列顺序应保存到专用 localStorage key，并兼容列增删',
    )
    assert(
      storeOrdersSource.includes('components={{ header: { cell: DraggableHeaderCell } }}') &&
        storeOrdersSource.includes('<SortableContext items={columnOrder} strategy={horizontalListSortingStrategy}>') &&
        storeOrdersSource.includes('<DndContext sensors={columnDragSensors} collisionDetection={closestCenter} onDragEnd={handleColumnDragEnd}>'),
      '列表页表格应接入可拖拽表头 cell 与横向 SortableContext',
    )
    assert(
      storeOrdersSource.includes('isStoreOrderListColumnOrderCustomized(columnOrder, draggableColumnKeys)') &&
        storeOrdersSource.includes('setColumnOrder(draggableColumnKeys)') &&
        storeOrdersSource.includes('localStorage.removeItem(STORE_ORDER_LIST_COLUMN_ORDER_STORAGE_KEY)'),
      '列表页拖拽列后应提供重置列按钮并清除本地列顺序',
    )
    assert(
      storeOrdersSource.includes('const draggableColumnKeys = baseColumns.map((column) => String(column.key) as StoreOrderListTableColumnKey)') &&
        storeOrdersSource.includes('rowSelection={') &&
        !storeOrdersSource.includes("columnOrder.includes('selection')"),
      '列表页选择列仍应由 rowSelection 管理，不能进入业务列拖拽顺序',
    )
    assert(
      compactCssSource.includes('.store-order-list-draggable-header') &&
        compactCssSource.includes('cursor: move') &&
        compactCssSource.includes('user-select: none'),
      '列表页拖拽表头应有局部样式，避免影响其他表格',
    )
  })
  if (listColumnDragFailure) failures.push(listColumnDragFailure)

  const detailColumnLayoutFailure = await runTest('详情页明细表应支持可访问的列头拖拽和列宽拖拽', () => {
    assert(
      detailSource.includes('DndContext') &&
        detailSource.includes('SortableContext') &&
        detailSource.includes('useSortable') &&
        detailSource.includes('horizontalListSortingStrategy'),
      '详情页明细表应复用 @dnd-kit 横向排序能力',
    )
    assert(
      detailSource.includes('KeyboardSensor') &&
        detailSource.includes('sortableKeyboardCoordinates') &&
        detailSource.includes('useSensor(KeyboardSensor, {') &&
        detailSource.includes('coordinateGetter: sortableKeyboardCoordinates'),
      '详情页列头拖拽应同时支持键盘传感器和横向坐标解析',
    )
    assert(
      // 重设计合并了列，旧 v1 布局对应另一套列，改用 v2 键。
      detailSource.includes("const STORE_ORDER_DETAIL_COLUMN_ORDER_STORAGE_KEY = 'hbweb_rv.storeOrders.detail.columnOrder.v2'") &&
        detailSource.includes("const STORE_ORDER_DETAIL_COLUMN_WIDTH_STORAGE_KEY = 'hbweb_rv.storeOrders.detail.columnWidths.v2'") &&
        detailSource.includes('localStorage.setItem(STORE_ORDER_DETAIL_COLUMN_ORDER_STORAGE_KEY') &&
        detailSource.includes('localStorage.setItem(STORE_ORDER_DETAIL_COLUMN_WIDTH_STORAGE_KEY'),
      '详情页明细表列顺序和列宽应保存到专用 localStorage key',
    )
    assert(
      detailSource.includes('components={{ header: { cell: DraggableHeaderCell } }}') &&
        detailSource.includes('<SortableContext items={detailSortableColumnKeys} strategy={horizontalListSortingStrategy}>') &&
        detailSource.includes('<DndContext sensors={detailColumnDragSensors} collisionDetection={closestCenter} onDragEnd={handleColumnDragEnd}>'),
      '详情页明细表应接入可拖拽表头 cell 与横向 SortableContext',
    )
    assert(
      detailSource.includes('handleColumnResizeStart') &&
        detailSource.includes('store-order-detail-column-resize-handle') &&
        detailSource.includes('aria-hidden="true"') &&
        detailSource.includes("document.body.style.cursor = 'col-resize'"),
      '详情页明细表应提供仅指针操作的表头列宽拖拽手柄',
    )
    assert(
      detailSource.includes("'data-column-fixed'?: 'left' | 'right'") &&
        detailSource.includes("const resizeFromLeft = props['data-column-fixed'] === 'right'") &&
        detailSource.includes("'data-column-fixed': column.fixed") &&
        detailSource.includes('resizeFromLeft ? -pointerDelta : pointerDelta') &&
        compactCssSource.includes('.store-order-detail-column-resize-handle-left'),
      '详情页固定右列表头应从左侧手柄反向调宽，并保留固定列定位信息',
    )
    assert(
      detailSource.includes('event.currentTarget.setPointerCapture(event.pointerId)') &&
        detailSource.includes('const stopDetailColumnResizeRef = useRef<(() => void) | null>(null)') &&
        detailSource.includes('stopDetailColumnResizeRef.current?.()') &&
        detailSource.includes('pointerEvent.pointerId !== pointerId') &&
        detailSource.includes("window.addEventListener('blur', finishResize, { once: true })") &&
        detailSource.includes("resizeHandle.addEventListener('lostpointercapture', finishResize, { once: true })") &&
        detailSource.includes("document.addEventListener('click', suppressHeaderClick, { capture: true, once: true })"),
      '详情页调宽应隔离 click，并在重复拖拽、失去捕获、窗口失焦或页面卸载时清理监听',
    )
    assert(
      // 重设计：货号、名称、条码合并为「商品」列（默认 320），并使用动态横向滚动宽度。
      detailSource.includes('product: 320') &&
        detailSource.includes('scroll={{ x: detailTableScrollX, y: 620 }}'),
      '详情页明细表商品列默认宽度应足够放下货号与名称，并使用动态横向滚动宽度',
    )
    assert(
      detailSource.includes("key: 'allocatedImportAmount'") &&
        detailSource.includes('allocatedImportAmount: 100'),
      '详情页列布局必须保留主线的已分配进口金额列及其默认宽度',
    )
    assert(
      detailSource.includes('className="wh-order-detail-product-cell"') &&
        detailSource.includes('className="wh-order-detail-item-number"') &&
        detailSource.includes('className="wh-order-detail-product-name" title={record.productName}') &&
        /\.wh-order-detail-item-number\s*\{[^}]*white-space:\s*nowrap/.test(detailCssSource) &&
        /\.wh-order-detail-product-name\s*\{[^}]*text-overflow:\s*ellipsis/.test(detailCssSource),
      '详情页商品列货号应完整显示、名称过长省略并可悬停查看，避免互相重叠',
    )
    assert(
      detailSource.includes('resetDetailColumnLayout') &&
        detailSource.includes("label: t('warehouseUi.storeOrderDetail.resetColumnLayout')") &&
        detailSource.includes('disabled: !isDetailColumnSettingsCustomized') &&
        detailSource.includes('localStorage.removeItem(STORE_ORDER_DETAIL_COLUMN_ORDER_STORAGE_KEY)') &&
        detailSource.includes('localStorage.removeItem(STORE_ORDER_DETAIL_COLUMN_WIDTH_STORAGE_KEY)'),
      '详情页明细表应在列设置菜单里提供重置列布局入口',
    )
    assert(
      compactCssSource.includes('.store-order-detail-draggable-header') &&
        compactCssSource.includes('.store-order-detail-draggable-header:focus-visible') &&
        compactCssSource.includes('.store-order-detail-column-resize-handle') &&
        compactCssSource.includes('cursor: col-resize'),
      '详情页紧凑样式应包含键盘焦点、拖拽表头和列宽手柄样式',
    )
  })
  if (detailColumnLayoutFailure) failures.push(detailColumnLayoutFailure)

  const listColumnResizeFailure = await runTest('列表页应隐藏无用发货列并支持全部业务列拖拽调宽', () => {
    assert(
      !storeOrdersSource.includes("dataIndex: 'totalAllocVolume'") &&
        !storeOrdersSource.includes("dataIndex: 'totalAllocQuantity'"),
      '列表页不应继续渲染发货体积和发货数量列',
    )

    // 改版后日期/数量/金额合并为两行列，默认列宽集中在 STORE_ORDER_LIST_DEFAULT_COLUMN_WIDTHS。
    const defaultWidthsBlock = storeOrdersSource.slice(
      storeOrdersSource.indexOf('const STORE_ORDER_LIST_DEFAULT_COLUMN_WIDTHS = {'),
      storeOrdersSource.indexOf('} as const', storeOrdersSource.indexOf('const STORE_ORDER_LIST_DEFAULT_COLUMN_WIDTHS = {')),
    )
    const defaultWidthExpectations = [
      ['orderNo', 140],
      ['storeCode', 120],
      ['orderOutboundDate', 104],
      ['flowStatus', 84],
      ['pickingAssignment', 150],
      ['quantityVolume', 88],
      ['orderShipAmount', 120],
      ['remarks', 110],
      ['action', 92],
    ] as const
    let defaultWidthTotal = readNumericValue(storeOrdersSource, /const STORE_ORDER_LIST_SELECTION_COLUMN_WIDTH = (\d+)/)
    for (const [columnKey, minimumWidth] of defaultWidthExpectations) {
      const width = readNumericValue(defaultWidthsBlock, new RegExp(`${columnKey}:\\s*(\\d+)`))
      assert(width >= minimumWidth, `${columnKey} 默认列宽应至少为 ${minimumWidth}px，避免两行内容被挤压`)
      assert(
        storeOrdersSource.includes(`width: STORE_ORDER_LIST_DEFAULT_COLUMN_WIDTHS.${columnKey},`),
        `${columnKey} 列应使用默认列宽常量`,
      )
      defaultWidthTotal += width
    }
    // 1440 宽屏：侧栏 248 + 内容区内边距 32 后约 1160px，留出滚动条余量，默认布局不应出现横向滚动。
    assert(defaultWidthTotal <= 1120, `默认列宽合计 ${defaultWidthTotal}px 超出 1440 宽屏可用宽度`)
    assert(!storeOrdersSource.includes("key: 'index'"), '列表页不应再有序号列')

    assert(
      storeOrdersSource.includes("const STORE_ORDER_LIST_COLUMN_WIDTH_STORAGE_KEY = 'hbweb_rv.storeOrders.list.columnWidths.v2'") &&
        storeOrdersSource.includes('const [columnWidths, setColumnWidths]') &&
        storeOrdersSource.includes('normalizeStoreOrderListColumnWidths(') &&
        storeOrdersSource.includes('hasSavedWidths = raw !== null') &&
        storeOrdersSource.includes('localStorage.setItem(STORE_ORDER_LIST_COLUMN_WIDTH_STORAGE_KEY'),
      '列表页列宽应使用独立 localStorage key 持久化，并过滤失效列宽',
    )
    assert(
        storeOrdersSource.includes('data-column-width') &&
        storeOrdersSource.includes('onColumnResizeStart: handleColumnResizeStart') &&
        storeOrdersSource.includes('store-order-list-column-resize-handle') &&
        storeOrdersSource.includes('<div className="store-order-list-draggable-header" {...attributes} {...listeners}>') &&
        !storeOrdersSource.includes('<th ref={setNodeRef} style={headerStyle} {...props} {...attributes} {...listeners}>'),
      '列表页每个业务列表头应使用独立调宽手柄，且不能误触列顺序拖拽',
    )
    assert(
      storeOrdersSource.includes('event.currentTarget.setPointerCapture(event.pointerId)') &&
        storeOrdersSource.includes('onClick={(event) => {') &&
        storeOrdersSource.includes('const stopColumnResizeRef = useRef<(() => void) | null>(null)') &&
        storeOrdersSource.includes('stopColumnResizeRef.current?.()') &&
        storeOrdersSource.includes('pointerEvent.pointerId !== pointerId') &&
        storeOrdersSource.includes("window.addEventListener('blur', finishResize, { once: true })") &&
        storeOrdersSource.includes("document.addEventListener('click', suppressHeaderClick, { capture: true, once: true })"),
      '调宽手柄应隔离表头排序 click，并在重复拖拽或页面卸载时清理监听',
    )
    assert(
      storeOrdersSource.includes("position: style?.position ?? 'relative'") &&
        storeOrdersSource.includes("'data-column-fixed': column.fixed === 'right' ? 'right' : undefined") &&
        storeOrdersSource.includes('const resizeFromLeft = props[\'data-column-fixed\'] === \'right\'') &&
        storeOrdersSource.includes('resizeFromLeft ? -pointerDelta : pointerDelta') &&
        compactCssSource.includes('.store-order-list-column-resize-handle-left'),
      '固定列表头应保留 sticky 定位，固定右列应从左侧反向调宽',
    )
    assert(
      storeOrdersSource.includes('(canUseWarehouseManagerActions ? STORE_ORDER_LIST_SELECTION_COLUMN_WIDTH : 0)') &&
        storeOrdersSource.includes('+ columns.reduce((total, column) => {') &&
        storeOrdersSource.includes('scroll={{ x: tableScrollX, y: 620 }}'),
      '列表页横向滚动宽度应只按当前业务列和实际存在的选择列计算',
    )
    assert(
      compactCssSource.includes('.store-order-list-column-resize-handle') &&
        compactCssSource.includes('cursor: col-resize') &&
        compactCssSource.includes('touch-action: none'),
      '列表页调宽手柄应提供独立命中区域和调宽光标',
    )
    assert(
      storeOrdersSource.includes('const isColumnSettingsCustomized = isColumnOrderCustomized || isColumnWidthCustomized') &&
        storeOrdersSource.includes('setColumnWidths({})') &&
        storeOrdersSource.includes('localStorage.removeItem(STORE_ORDER_LIST_COLUMN_WIDTH_STORAGE_KEY)'),
      '手动调整顺序或宽度后，重置列应恢复全部默认列设置',
    )
  })
  if (listColumnResizeFailure) failures.push(listColumnResizeFailure)

  const listStatusFilterFailure = await runTest('列表页状态筛选应使用状态页签并默认进行中（已提交 + 配货中）', () => {
    // 改版：状态只保留顶部页签一个入口，去掉状态复选框和状态列头筛选；默认口径不变。
    assert(storeOrdersSource.includes('<StatusTabs'), '状态筛选应使用 StatusTabs 页签')
    assert(!storeOrdersSource.includes('Checkbox.Group'), '状态筛选不应再保留复选框')
    assert(!storeOrdersSource.includes('makeStatusFilterDropdown'), '状态列不应再有列头筛选')
    assert(
      storeOrdersSource.includes('useState<StoreOrderStatusTabKey>(DEFAULT_STORE_ORDER_STATUS_TAB)') &&
        listLogicSource.includes("export const DEFAULT_STORE_ORDER_STATUS_TAB: StoreOrderStatusTabKey = 'active'") &&
        listLogicSource.includes('active: [StoreOrderFlowStatus.Submitted, StoreOrderFlowStatus.Picking],'),
      '默认页签应为进行中，即已提交和配货中',
    )
    assert(
      listLogicSource.includes('all: STORE_ORDER_COUNTED_STATUSES,') &&
        listLogicSource.includes('StoreOrderFlowStatus.Submitted,\n  StoreOrderFlowStatus.Picking,\n  StoreOrderFlowStatus.Completed,\n]'),
      '全部页签应只含已提交、配货中、已完成三种状态，不含购物车',
    )
    assert(
      storeOrdersSource.includes(': getStoreOrderStatusTabStatusList(statusTab),') &&
        storeOrdersSource.includes('statusList: getStoreOrderStatusTabStatusList(nextTab)'),
      '列表查询的 statusList 应只由当前页签决定，切换页签立即查询',
    )
    assert(
      storeOrdersSource.includes('const statusCountRequestGuardRef = useRef(createLatestRequestGuard())') &&
        storeOrdersSource.includes('statusCountRequestGuardRef.current.invalidate()') &&
        storeOrdersSource.includes('buildStoreOrderStatusCountQuery(query, status)') &&
        listLogicSource.includes('pageSize: 1,'),
      '页签计数应按状态并行发 pageSize=1 请求，并有独立的最新请求守卫',
    )
  })
  if (listStatusFilterFailure) failures.push(listStatusFilterFailure)

  const listColumnFilterFailure = await runTest('列表页筛选条件应走服务端查询参数，收进更多筛选并在已生效条显示', () => {
    // 改版：原列头放大镜里的条件收进「更多筛选」，仍按服务端 columnFilters 查询；生效条件统一显示在已生效筛选条。
    assert(storeOrdersSource.includes('StoreOrderListColumnFilters'), '列表页应引入列筛选类型')
    assert(storeOrdersSource.includes('const [columnFilters, setColumnFilters] = useState<StoreOrderListColumnFilters>({})'), '列表页应维护列筛选状态')
    assert(storeOrdersSource.includes('columnFilters: cleanStoreOrderListColumnFilters('), '列表查询应携带清理后的 columnFilters')
    assert(storeOrdersSource.includes('setColumnFilters({})'), '清空全部应清空列筛选状态')
    assert(storeOrdersSource.includes('columnFilters: undefined'), '清空全部的查询应显式清空服务端列筛选参数')
    assert(!storeOrdersSource.includes('filterDropdown'), '列表页主表不应再有列头放大镜筛选')
    assert(storeOrdersSource.includes('<MoreFiltersButton'), '低频条件应收进更多筛选')
    for (const field of [
      "'outboundDateStart', 'outboundDateEnd'",
      "'totalQuantityMin', 'totalQuantityMax'",
      "'totalOrderAmountMin', 'totalOrderAmountMax'",
      "'totalOrderVolumeMin', 'totalOrderVolumeMax'",
      "'importTotalAmountMin', 'importTotalAmountMax'",
      "'createdAtStart', 'createdAtEnd'",
      "'updatedAtStart', 'updatedAtEnd'",
      "'remarks')",
      "'updatedBy')",
    ]) {
      assert(storeOrdersSource.includes(field), `更多筛选应保留原列头筛选条件：${field}`)
    }
    assert(
      storeOrdersSource.includes('<ActiveFilterBar items={activeFilterItems} onClearAll={clearAllFilters} />') &&
        storeOrdersSource.includes('removeStoreOrderMoreFilterGroup(columnFilters, group)'),
      '生效条件应显示在已生效筛选条，并可逐个移除和清空全部',
    )
    assert(
      storeOrdersSource.includes('const STORE_ORDER_KEYWORD_DEBOUNCE_MS = 300') &&
        storeOrdersSource.includes('void loadDataRef.current?.({ pageNumber: 1, keyword: nextKeyword || undefined })') &&
        !storeOrdersSource.includes("t('common.query')"),
      '关键字应防抖即时查询（走 current loader 取最新筛选），不再需要查询按钮',
    )
    assert(compactCssSource.includes('.store-order-list-column-filter'), '详情页列头筛选弹层仍复用这份局部紧凑样式')
  })
  if (listColumnFilterFailure) failures.push(listColumnFilterFailure)

  // 重设计：货号、名称、条码、零售价合并为「商品」列（第二行「条码 · 零售 $x」），行操作收进 ⋯ 菜单。
  const detailContentFailure = await runTest('详情页商品列应合并货号条码名称并保留业务可读性', () => {
    const productColumn = detailMainTableSource.slice(
      detailMainTableSource.indexOf("key: 'product'"),
      detailMainTableSource.indexOf("key: 'locationCode'"),
    )
    assert(productColumn.includes('width={32}') && productColumn.includes('height={32}'), '详情页商品列图片应不超过 32x32')
    assert(productColumn.includes('className="store-order-detail-copy-button"'), '详情页货号复制按钮应为无文字图标按钮')
    assert(!productColumn.includes('<Button size="small" type="link" onClick={() => void copyTextToClipboard(value)}>'), '详情页主明细货号复制按钮不应显示复制文字')
    assert(productColumn.includes('onClick={() => void copyTextToClipboard(record.itemNumber)}'), '详情页商品列应保留货号复制')
    assert(
      productColumn.includes("{record.barcode || '--'} · {t('warehouseUi.storeOrderDetail.retailPrice', { price: formatCurrencyAmount(record.price) })}"),
      '详情页商品列第二行应显示条码与零售价',
    )
    assert(/\.wh-order-detail-product-sub\s*\{[^}]*white-space:\s*nowrap/.test(detailCssSource), '条码与零售价应单行显示')
    assert(detailMainTableSource.includes("key: 'actions'") && detailMainTableSource.includes('render: (_, record) => renderLineMoreDropdown(record)'), '详情页行操作应收进 ⋯ 菜单')
    assert(
      detailSource.includes("label: t('warehouseUi.storeOrderDetail.rowSave')") &&
        detailSource.includes("label: t('warehouseUi.storeOrderDetail.rowDelete')") &&
        detailSource.includes('void handleSaveLine(record)') &&
        detailSource.includes('void handleToggleLineStatus(record)') &&
        detailSource.includes('confirmRemoveLine(record)'),
      '⋯ 菜单应保留只保存此行、仓库上/下架与删除行',
    )
    assert(
      detailSource.includes("title: t('storeOrders.detail.confirmDeleteLine'),") && detailSource.includes('onOk: () => handleRemoveLine(line),'),
      '删除行改为菜单项后仍需二次确认',
    )
    assert(detailSource.includes('className={`store-order-detail-action-button'), '详情页行 ⋯ 按钮应使用紧凑图标按钮样式')
  })
  if (detailContentFailure) failures.push(detailContentFailure)

  const detailProductStatusCopyFailure = await runTest('详情页商品状态应使用上下架文案', () => {
    const statusColumn = readColumnBlock(detailMainTableSource, 'isActive')

    assert(statusColumn.includes("t('common.activeUpper')") && statusColumn.includes("t('common.inactiveUpper')"), '详情页商品状态列应显示上架/下架')
    // 重设计：行上的上/下架进 ⋯ 菜单，文案写明改的是仓库商品全局状态（下架仍先填供货说明）。
    assert(
      detailSource.includes("? t('warehouseUi.storeOrderDetail.rowDelistWarehouse')") &&
        detailSource.includes(": t('warehouseUi.storeOrderDetail.rowListWarehouse')") &&
        detailMessagesZhSource.includes('"rowListWarehouse": "仓库上架（改仓库商品全局状态）"') &&
        detailMessagesZhSource.includes('"rowDelistWarehouse": "仓库下架（改仓库商品全局状态，需填供货说明）…"'),
      '详情页商品状态切换菜单应提示上架/下架，并写明改的是仓库全局商品状态',
    )
    assert(detailSource.includes("status: line.isActive ? t('common.inactiveUpper') : t('common.activeUpper')"), '详情页商品状态切换成功提示应使用上架/下架')
    assert(detailSource.includes("{ value: 'active', label: t('common.activeUpper') }") && detailSource.includes("{ value: 'inactive', label: t('common.inactiveUpper') }"), '批量修改状态下拉应使用上架/下架')
  })
  if (detailProductStatusCopyFailure) failures.push(detailProductStatusCopyFailure)

  const containerPickerRetailPriceFailure = await runTest('货柜选品弹窗商品表格应展示零售价列', () => {
    const retailPriceColumn = readColumnBlock(containerProductPickerSource, '零售价格')
    const importPricePosition = containerProductPickerSource.indexOf("title: t('column.importPrice')")
    const retailPricePosition = containerProductPickerSource.indexOf("title: t('column.retailPrice')")
    const containerQtyPosition = containerProductPickerSource.indexOf("title: t('column.containerQty')")

    assert(retailPricePosition > importPricePosition, '零售价列应位于进口价列之后')
    assert(retailPricePosition < containerQtyPosition, '零售价列应位于货柜数量列之前')
    assert(retailPriceColumn.includes("title: t('column.retailPrice')"), '零售价列应使用 column.retailPrice 翻译')
    assert(retailPriceColumn.includes('record.商品信息?.零售价格'), '零售价列应读取商品信息中的零售价格')
    assert(retailPriceColumn.includes("value === undefined || value === null ? '--' : Number(value).toFixed(2)"), '零售价列缺失显示 --，有效值应保留两位')
    assert(!containerProductPickerSource.includes('retailPrice:'), '货柜选品加入订单 payload 不应写入零售价')
  })
  if (containerPickerRetailPriceFailure) failures.push(containerPickerRetailPriceFailure)

  const densityFailure = await runTest('详情页主明细表关键字段应默认可读并保留紧凑输入列', () => {
    // 重设计：图片并入「商品」列（不再单独占列）；去掉序号列后最小横向宽度相应收窄到 1080。
    const productColumn = detailMainTableSource.slice(
      detailMainTableSource.indexOf("key: 'product'"),
      detailMainTableSource.indexOf("key: 'locationCode'"),
    )
    const locationColumn = readColumnBlock(detailMainTableSource, 'locationCode')
    const allocQuantityColumn = readColumnBlock(detailMainTableSource, 'allocQuantity')
    const importPriceColumn = readColumnBlock(detailMainTableSource, 'importPrice')

    assert(readNumericValue(productColumn, /width=\{(\d+)\}/) <= 32, '图片宽度应压到 32 以内')
    assert(readNumericValue(productColumn, /height=\{(\d+)\}/) <= 32, '图片高度应压到 32 以内')
    assert(detailSource.includes('product: 320'), '商品列默认宽度应放得下图片、货号与名称')
    assert(detailSource.includes('scroll={{ x: detailTableScrollX, y: 620 }}'), '主表 scroll.x 应基于当前列宽动态计算')
    assert(detailSource.includes('STORE_ORDER_DETAIL_TABLE_MIN_SCROLL_X = 1080'), '主表动态 scroll.x 应保留最小宽度')
    assert(locationColumn.includes('width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.locationCode'), '货位列应继续走默认紧凑列宽常量')
    assert(readNumericValue(allocQuantityColumn, /style=\{\{\s*width:\s*(\d+)/) <= 62, '发货数输入框宽度应压到 62 以内')
    assert(readNumericValue(importPriceColumn, /style=\{\{\s*width:\s*(\d+)/) <= 62, '进口价输入框宽度应压到 62 以内')
    assert(importPriceColumn.includes('controls={false}'), '进口价输入框应隐藏加减按钮，避免误触改价')
  })
  if (densityFailure) failures.push(densityFailure)

  const cssFailure = await runTest('局部 CSS 应提供紧凑表格、两行文本、nowrap 和等宽数字规则', () => {
    const barcodeCellRule = readCssRule(compactCssSource, '.store-order-detail-table .store-order-barcode-cell')
    const barcodeTextRule = readCssRule(compactCssSource, '.store-order-detail-table .store-order-barcode-cell .ant-typography')
    const inputNumberRule = readCssRule(compactCssSource, '.store-order-detail-table .ant-input-number')
    const detailCellRule = readCssRule(compactCssSource, '.store-order-detail-table .ant-table-cell')

    assert(compactCssSource.includes('.store-order-detail-table .ant-table-cell'), '详情表格缺少局部 cell padding 规则')
    assert(compactCssSource.includes('.store-order-list-table .store-order-list-order-cell'), '列表订单号列缺少局部防溢出样式')
    assert(compactCssSource.includes('.store-order-list-table .wh-orders-store-name'), '列表分店列缺少名称省略样式')
    assert(compactCssSource.includes('.store-order-list-table .store-order-two-line-text'), '列表备注列缺少两行截断样式')
    assert(!/^\\.store-order-nowrap/m.test(compactCssSource), 'nowrap 工具类必须限定到详情主表下')
    assert(!/^\\.store-order-numeric-cell/m.test(compactCssSource), '数字工具类必须限定到详情主表下')
    assert(!/^\\.store-order-two-line-text/m.test(compactCssSource), '两行文本工具类必须限定到详情主表下')
    assert(compactCssSource.includes('-webkit-line-clamp: 2'), '紧凑样式缺少最多两行规则')
    assert(compactCssSource.includes('white-space: nowrap'), '紧凑样式缺少 nowrap 规则')
    assert(compactCssSource.includes('font-variant-numeric: tabular-nums'), '紧凑样式缺少等宽数字规则')
    assert(compactCssSource.includes('.store-order-detail-filter-bar'), '详情筛选统计条缺少紧凑样式')
    assert(compactCssSource.includes('.store-order-detail-table .store-order-barcode-cell .ant-typography'), '条码文本缺少不隐藏不折叠样式')
    assert(/vertical-align:\s*middle/.test(detailCellRule), '详情主表单元格应垂直居中')
    assert(/white-space:\s*nowrap/.test(barcodeCellRule), '条码容器应强制单行，避免条码图片和文本换行')
    assert(/overflow:\s*visible/.test(barcodeCellRule), '条码容器不应隐藏超出内容')
    assert(/text-overflow:\s*clip/.test(barcodeTextRule), '条码文本不应省略隐藏')
    assert(/white-space:\s*nowrap/.test(inputNumberRule), '详情主表输入型数字列应保持单行')
  })
  if (cssFailure) failures.push(cssFailure)

  const detailTableStripeFailure = await runTest('详情页主明细表应有隔行色并保持固定列和 hover 一致', () => {
    assert(
      compactCssSource.includes('.store-order-detail-table .ant-table-tbody > tr:nth-child(even) > td'),
      '详情主表缺少偶数行隔行色规则',
    )
    assert(
      compactCssSource.includes('.store-order-detail-table .ant-table-tbody > tr:nth-child(odd) > td'),
      '详情主表缺少奇数行隔行色规则',
    )
    assert(
      compactCssSource.includes('.store-order-detail-table .ant-table-tbody > tr:hover > td'),
      '详情主表缺少 hover 行背景规则',
    )
    assert(compactCssSource.includes('.ant-table-cell-fix-left'), '详情主表固定左列背景应跟随行背景')
    assert(compactCssSource.includes('.ant-table-cell-fix-right'), '详情主表固定右列背景应跟随行背景')
    assert(!/^\\.ant-table-tbody\s*>/m.test(compactCssSource), '隔行色规则必须限定在详情主表下')
  })
  if (detailTableStripeFailure) failures.push(detailTableStripeFailure)

  const detailTableVerticalAlignFailure = await runTest('详情页主明细表内部元素应垂直居中', () => {
    assert(
      compactCssSource.includes('.store-order-detail-table .ant-table-tbody > tr > td .ant-space'),
      '详情主表 Space 内容应垂直居中',
    )
    assert(
      compactCssSource.includes('.store-order-detail-table .ant-table-tbody > tr > td .ant-image'),
      '详情主表图片内容应垂直居中',
    )
    assert(
      compactCssSource.includes('.store-order-detail-table .ant-table-tbody > tr > td .ant-tag'),
      '详情主表状态标签应垂直居中',
    )
    assert(
      compactCssSource.includes('.store-order-detail-table .ant-table-tbody > tr > td .ant-input-number'),
      '详情主表数字输入框应垂直居中',
    )
    assert(compactCssSource.includes('.store-order-detail-table .store-order-two-line-text'), '详情主表两行文本应保留局部样式')
    assert(compactCssSource.includes('align-items: center'), '详情主表内部 flex 元素缺少居中对齐')
  })
  if (detailTableVerticalAlignFailure) failures.push(detailTableVerticalAlignFailure)

  const detailBulkSaveFailure = await runTest('详情页应提供整单保存且只提交已修改明细行', () => {
    assert(detailSource.includes('handleSaveEditedLines'), '详情页缺少整单保存处理函数')
    assert(detailSource.includes('getEditedLinePayloads()'), '整单保存应从已修改行生成 payload')
    assert(detailSource.includes('batchUpdateStoreOrderLines({'), '整单保存应复用明细批量保存接口')
    assert(detailSource.includes('detailGUID: item.detailGUID'), '整单保存 payload 应携带明细 GUID 以命中后端快路径')
    // 重设计：整单保存改为吸底「未保存修改」条里的「保存 N 行」，有草稿时才出现。
    assert(detailSource.includes("t('warehouseUi.storeOrderDetail.saveLines', { count: editedLineCount })"), '详情页缺少整单保存（保存 N 行）按钮文案')
    assert(detailSource.includes('{canUseWarehouseManagerActions && editedLineCount > 0 ? ('), '未保存修改条应只在有草稿且有订货管理权限时出现')
    assert(
      detailSource.includes('disabled={isReadonlyOrder || isPasteOptimisticPreviewActive || editedLineCount === 0}'),
      '整单保存应在只读、临时预览或无修改时禁用',
    )
    assert(detailSource.includes('setEditingRows((current) => {') && detailSource.includes('savedDetailGUIDs'), '整单保存成功后应清理已保存行编辑状态')
  })
  if (detailBulkSaveFailure) failures.push(detailBulkSaveFailure)

  const detailRefreshImportPriceFailure = await runTest('详情页应允许仓库管理员二次确认后从仓库表更新进货价', () => {
    assert(detailSource.includes('refreshStoreOrderImportPrices'), '详情页应调用更新进货价专用服务')
    assert(detailSource.includes('handleRefreshImportPricesFromWarehouse'), '详情页缺少更新进货价处理函数')
    assert(detailSource.includes("t('storeOrders.detail.refreshImportPrices'"), '详情页缺少更新进货价按钮文案')
    assert(
      detailSource.includes('detailGUIDs: isSelectedScope ? targetDetailGUIDs : undefined'),
      '有选中行时应传明细 GUID，未选中时应交给后端整单刷新',
    )
    assert(
      detailSource.includes("t('storeOrders.detail.refreshImportPricesSelectedContent'") &&
        detailSource.includes("t('storeOrders.detail.refreshImportPricesWholeOrderContent'"),
      '更新进货价二次确认应区分选中行和整单范围',
    )
    assert(
      detailSource.includes('disabled={!detail || isPasteOptimisticPreviewActive || refreshImportPriceLoading}'),
      '更新进货价按钮不应因为 isReadonlyOrder 禁用，但临时预览期间应等待后台刷新后再操作',
    )
  })
  if (detailRefreshImportPriceFailure) failures.push(detailRefreshImportPriceFailure)

  const warehouseManagerActionFailure = await runTest('仓库员工仅可看到详情页只读文档入口，不应看到订货管理功能按钮', () => {
    // 重设计：配货单/发票/状态流转在概况卡右侧；明细管理入口在明细卡工具栏、「添加商品」菜单、勾选条与吸底保存条。
    const overviewActionsSource = detailSource.slice(
      detailSource.indexOf('<div className="wh-order-detail-overview-actions">'),
      detailSource.indexOf('<ol className="wh-order-detail-steps"'),
    )
    const linesToolbarSource = detailSource.slice(
      detailSource.indexOf('<div className="wh-order-detail-toolbar">'),
      detailSource.indexOf('<DndContext sensors={detailColumnDragSensors}'),
    )
    const addProductMenuSource = detailSource.slice(
      detailSource.indexOf("const addProductMenuItems: MenuProps['items'] = ["),
      detailSource.indexOf('const handleAddProductMenuClick'),
    )
    const pickingButtonPosition = overviewActionsSource.indexOf("t('storeOrders.pickingList')")
    const pickingButtonSource = overviewActionsSource.slice(
      overviewActionsSource.lastIndexOf('<Button', pickingButtonPosition),
      pickingButtonPosition,
    )
    const managerGuardText = '{canUseWarehouseManagerActions ? ('
    const detailExtraGuardText = '{canUseStoreOrderDetailExtraActions ? ('
    const isInsideGuard = (source: string, guardText: string, targetPosition: number) => {
      const guardPosition = source.lastIndexOf(guardText, targetPosition)
      const guardClosePosition = source.lastIndexOf(') : null}', targetPosition)
      return guardPosition >= 0 && guardPosition > guardClosePosition
    }
    const invoiceButtonPosition = overviewActionsSource.indexOf("t('storeOrders.invoice')")
    const managerOnlyDetailActions = [
      "t('warehouseUi.storeOrderDetail.quickAddButton')",
      'menu={{ items: addProductMenuItems, onClick: handleAddProductMenuClick }}',
      "t('storeOrders.detail.refreshImportPrices')",
      '<SelectionActionBar',
      "t('warehouseUi.storeOrderDetail.copyOrderQtyToAlloc')",
      "t('warehouseUi.storeOrderDetail.batchModify')",
    ]

    assert(
      storeOrdersSource.includes('const isWarehouseStaffOnly =') &&
        storeOrdersSource.includes('const canUseWarehouseManagerActions = access.canManageWarehouseOrders && !isWarehouseStaffOnly') &&
        storeOrdersSource.includes('const canCreateStoreOrder = access.canWriteOrder || canUseWarehouseManagerActions') &&
        storeOrdersSource.includes('const canDeleteStoreOrder = access.canDeleteOrder || canUseWarehouseManagerActions'),
      '列表页应使用仓库订货管理权限开关，并排除纯 WarehouseStaff 写权限',
    )
    // HQ 增量同步按钮已随 HQ → HBweb 同步于 2026-09-29 停用，不再出现在列表页。
    assert(
      !storeOrdersSource.includes("t('storeOrders.syncIncrementalOrders')"),
      '列表页不应再显示 HQ 增量同步按钮',
    )
    // 改版：修复 GUID 收进页头 ⋯ 菜单；复制、批量改状态、分配拣货只在勾选后出现在勾选条（勾选列仅管理员有）；
    // 行内复制/改状态/发票收进 ⋯ 菜单，仍受同样的权限开关控制。
    const selectionBarSource = storeOrdersSource.slice(
      storeOrdersSource.indexOf('{canUseWarehouseManagerActions && selectedOrders.length ? ('),
      storeOrdersSource.indexOf('</SelectionActionBar>'),
    )
    const rowMenuSource = storeOrdersSource.slice(
      storeOrdersSource.indexOf('const buildRowMenuItems = (record: StoreOrderListItem)'),
      storeOrdersSource.indexOf('const handleRowMenuAction ='),
    )
    assert(
      storeOrdersSource.includes('{canUseWarehouseManagerActions ? (') &&
        storeOrdersSource.includes("...(canUseWarehouseManagerActions\n      ? [{ key: 'fixStoreGuid'") &&
        storeOrdersSource.includes("t('storeOrders.fixStoreGuid', '修复分店 GUID')") &&
        storeOrdersSource.includes("t('storeOrders.newOrder')") &&
        storeOrdersSource.includes('disabled={!canCreateStoreOrder}') &&
        storeOrdersSource.includes('{canDeleteStoreOrder ? ('),
      '列表页修复、新建、删除入口应仅仓库订货管理权限可见',
    )
    assert(
      selectionBarSource.includes("t('warehouseUi.storeOrders.assignPicking')") &&
        selectionBarSource.includes("t('warehouseUi.storeOrders.changeStatus')") &&
        selectionBarSource.includes('handleBatchStatusChange(') &&
        selectionBarSource.includes('[FlowStatus.Submitted, FlowStatus.Completed]') &&
        selectionBarSource.includes("t('warehouseUi.storeOrders.copyAsNew')") &&
        selectionBarSource.includes('disabled={!canCopySelection}'),
      '勾选条应提供分配拣货、批量改已提交/已完成与复制为新订单，且仅仓库订货管理权限可见',
    )
    assert(
      storeOrdersSource.includes('const canCopySelection = canCopySelectedStoreOrders(selectedOrders.length)') &&
        storeOrdersSource.includes('const sourceOrderGUID = copySourceOrder.orderGUID') &&
        !storeOrdersSource.includes('String(selectedRowKeys[0])'),
      '复制订单应只在恰好勾选 1 单时可用，并以弹窗打开时指定的订单为源',
    )
    assert(
      rowMenuSource.includes('if (canUseWarehouseManagerActions) {\n      documentItems.push({ key: \'invoice\'') &&
        rowMenuSource.includes("if (canUseWarehouseManagerActions) {\n      manageItems.push({ key: 'copy'") &&
        rowMenuSource.includes('if (canDeleteStoreOrder) {'),
      '行内 ⋯ 菜单的发票、复制、改状态、删除应受原权限开关控制',
    )
    assert(
      !storeOrdersSource.includes('onClick={() => handleStatusToggle(record)}') &&
        storeOrdersSource.includes("case 'markSubmitted':\n        handleStatusToggle(record)") &&
        storeOrdersSource.includes('<StatusPill tone={getStoreOrderStatusPillTone(value)}>'),
      '状态列只展示状态胶囊，单张改状态改由行内菜单明确触发并保留确认弹窗',
    )
    assert(
      storeOrdersSource.includes("t('warehouseUi.storeOrders.pageTotal'") &&
        listMessagesZhSource.includes('"pageTotal": "本页合计 · {{count}} 单"'),
      '表尾合计只基于当前页数据，文案必须是「本页合计」',
    )
    assert(
      storeOrdersSource.includes('canUseWarehouseManagerActions && (record.flowStatus === FlowStatus.Submitted || record.flowStatus === FlowStatus.Picking)'),
      '列表页配货入口应仅仓库管理员可见',
    )
    assert(
      storeOrdersSource.includes('rowSelection={\n                canUseWarehouseManagerActions'),
      '列表页勾选列应仅仓库管理员可见',
    )
    assert(
      detailSource.includes('const isWarehouseStaffOnly =') &&
        detailSource.includes('const canUseWarehouseManagerActions = access.canManageWarehouseOrders && !isWarehouseStaffOnly'),
      '详情页应使用仓库订货管理权限开关，并排除纯 WarehouseStaff 写权限',
    )
    assert(
      detailSource.includes('const canUseStoreOrderDocumentActions = access.isWarehouseStaff'),
      '详情页应为 WarehouseStaff 提供只读文档入口权限开关',
    )
    assert(
      detailSource.includes('const canUseStoreOrderDetailExtraActions = canUseWarehouseManagerActions || canUseStoreOrderDocumentActions'),
      '详情页明细卡片 extra 应同时允许仓库管理员和 WarehouseStaff 文档入口，避免中文仓库经理被误隐藏',
    )
    assert(
      detailSource.includes('if (canUseWarehouseManagerActions && canEditOrder)'),
      '详情页编辑保护应同时检查仓库管理员权限',
    )
    assert(
      overviewActionsSource.includes('{canUseWarehouseManagerActions ? (\n                  <Dropdown') &&
        overviewActionsSource.includes('menu={{ items: overviewMenuItems, onClick: handleOverviewMenuClick }}') &&
        detailSource.includes('const flowActions = resolveStoreOrderDetailFlowActions(detail?.flowStatus, canUseWarehouseManagerActions)'),
      '详情页状态流转（开始配货/完成订单/更改状态）应仅仓库管理员可见',
    )
    assert(
      overviewActionsSource.indexOf(detailExtraGuardText) >= 0 &&
        overviewActionsSource.indexOf(detailExtraGuardText) < pickingButtonPosition &&
        isInsideGuard(overviewActionsSource, detailExtraGuardText, pickingButtonPosition) &&
        !isInsideGuard(overviewActionsSource, managerGuardText, pickingButtonPosition) &&
        pickingButtonSource.includes('navigate(`/warehouse/store-order/picking/${detail.orderGUID}`)') &&
        pickingButtonSource.includes('icon={<PrinterOutlined />}'),
      '详情页配货单按钮应受只读文档入口权限控制，不能只由仓库管理员权限包住',
    )
    assert(
      invoiceButtonPosition > 0 && isInsideGuard(overviewActionsSource, managerGuardText, invoiceButtonPosition),
      '详情页发票按钮仍应仅仓库管理员可见',
    )
    assert(
      managerOnlyDetailActions.every((actionText) => {
        const actionPosition = linesToolbarSource.indexOf(actionText)
        return actionPosition > 0 && isInsideGuard(linesToolbarSource, managerGuardText, actionPosition)
      }),
      '详情页明细管理功能按钮应继续受仓库管理员权限保护',
    )
    assert(
      addProductMenuSource.includes("t('storeOrders.selectProduct')") &&
        addProductMenuSource.includes("t('warehouseUi.storeOrderDetail.addFromContainer')") &&
        addProductMenuSource.includes("t('storeOrders.excelPaste')"),
      '「添加商品」菜单应保留选择商品、货柜选择与 Excel 粘贴三个入口',
    )
    assert(
      detailSource.includes("column.key !== 'actions'") &&
        /rowSelection=\{\s*canUseWarehouseManagerActions/.test(detailSource),
      '详情页行操作列和勾选列应仅仓库管理员可见',
    )
    assert(
      detailSource.includes('disabled={!canUseWarehouseManagerActions || isReadonlyOrder}') &&
        detailSource.includes('disabled={!canUseWarehouseManagerActions || !canEditOutboundDate}'),
      '详情页非仓库管理员应不能编辑订单头和明细输入',
    )
  })
  if (warehouseManagerActionFailure) failures.push(warehouseManagerActionFailure)

  const importPriceConfirmFailure = await runTest('详情页保存进口价变更前应提示同步仓库商品表和分店表', () => {
    assert(detailSource.includes('confirmImportPriceSync'), '详情页缺少进口价同步确认 helper')
    assert(detailSource.includes("t('storeOrders.detail.importPriceSyncConfirmTitle'"), '进口价同步确认缺少标题文案')
    assert(detailSource.includes("t('storeOrders.detail.importPriceSyncConfirmContent'"), '进口价同步确认缺少内容文案')
    assert(detailSource.includes('Checkbox') && detailSource.includes('defaultChecked'), '进口价同步确认应提供默认勾选的 Checkbox')
    assert(detailSource.includes("t('storeOrders.detail.syncImportPriceCheckbox'"), '进口价同步确认缺少勾选文案')
    assert(detailSource.includes('getEditedLinePayloads(syncImportPrice)'), '整单保存应按勾选状态决定是否提交进口价')
    assert(detailSource.includes('importPrice: importPriceChanged ? importPrice : undefined'), '单行保存应始终提交已变更的订单明细进口价')
    assert(detailSource.includes('syncImportPrice: importPriceChanged ? syncImportPrice : undefined'), '单行保存应单独提交商品/分店同步开关')
    assert(detailSource.includes('importPrice: importPriceChanged ? edited.importPrice : undefined'), '整单保存应始终提交已变更的订单明细进口价')
    assert(detailSource.includes('syncImportPrice: importPriceChanged ? syncImportPrice : undefined'), '整单保存应单独提交商品/分店同步开关')
    assert(detailSource.includes('hasImportPriceChanged(line)'), '单行保存应判断进口价是否变更')
    assert(detailSource.includes('payloads.some((item) => item.importPriceChanged)'), '整单保存应判断本次是否包含进口价变更')
  })
  if (importPriceConfirmFailure) failures.push(importPriceConfirmFailure)

  const batchCopyOrderQuantityFailure = await runTest('详情页批量修改应支持把订货数量复制给发货数量', () => {
    const copyBranchStart = detailSource.indexOf("} else if (payload.type === 'copyOrderQuantityToAllocQuantity' && copyOrderQuantityPayload)")
    const copyBranchEnd = detailSource.indexOf('} else {', copyBranchStart + 1)
    const copyBranchSource = detailSource.slice(copyBranchStart, copyBranchEnd)

    assert(
      detailSource.includes('buildBatchCopyOrderQuantityPayload') &&
        detailSource.includes('shouldSubmitBatchCopyOrderQuantity') &&
        detailSource.includes("from './batchCopyOrderQuantity'"),
      '详情页应复用批量复制订货数 helper',
    )
    assert(detailSource.includes("'copyOrderQuantityToAllocQuantity'"), '批量修改类型应包含复制订货数量到发货数量')
    assert(detailSource.includes("t('storeOrders.batchCopyOrderQuantityToAllocQuantity')"), '批量弹窗应展示复制订货数量到发货数量选项')
    assert(detailSource.includes("payload.type === 'copyOrderQuantityToAllocQuantity'"), '批量确认应处理复制订货数量分支')
    assert(copyBranchSource.includes('const changedCopyLines = selectedLines.filter'), '复制订货数量分支应计算实际变化行数')
    assert(copyBranchSource.includes('setEditingRows((current) => {'), '复制订货数量分支应只写页面草稿')
    assert(copyBranchSource.includes('changedCopyLines.forEach'), '复制订货数量分支应只把实际变化行写入发货数草稿')
    assert(copyBranchSource.includes('allocQuantity: Number(line.quantity ?? 0)'), '复制订货数量分支应把订货数量写入发货数草稿')
    assert(!copyBranchSource.includes('batchUpdateStoreOrderLines('), '复制订货数量分支不应立即提交后端')
    assert(!copyBranchSource.includes('loadDetail('), '复制订货数量分支不应立即刷新后端数据')
    assert(detailSource.includes("t('storeOrders.batchCopyOrderQuantityDraftSuccess'"), '复制草稿成功后应提示用户点击整单保存')
    assert(detailSource.includes("t('storeOrders.batchCopyOrderQuantityNoChange')"), '复制后无实际变化时应提示未产生新的发货数变更')
    assert(detailSource.includes("handleBatchConfirm({ type: 'copyOrderQuantityToAllocQuantity' })"), '页面批量复制按钮应复用同一个批量确认分支')
    assert(detailSource.includes('detailGUID: line.detailGUID'), '复制订货数量 payload 应携带明细 GUID 以命中后端快路径')
    assert(detailSource.includes("t('storeOrders.batchCopyOrderQuantityConfirmTitle')"), '风险行应弹出二次确认标题')
    // 重设计：「批量复制」改名「发货数 = 订货数」，只在勾选后出现在勾选条里（原「放在配货单前面」的位置约束随之失效）。
    const selectionBarSource = detailSource.slice(
      detailSource.indexOf('<SelectionActionBar selectedCount={selectedLineKeys.length}'),
      detailSource.indexOf('</SelectionActionBar>'),
    )
    assert(
      selectionBarSource.includes("t('warehouseUi.storeOrderDetail.copyOrderQtyToAlloc')") &&
        selectionBarSource.includes("handleBatchConfirm({ type: 'copyOrderQuantityToAllocQuantity' })") &&
        detailMessagesZhSource.includes('"copyOrderQtyToAlloc": "发货数 = 订货数"'),
      '详情页勾选条应提供「发货数 = 订货数」批量复制入口',
    )
  })
  if (batchCopyOrderQuantityFailure) failures.push(batchCopyOrderQuantityFailure)

  // 重设计：两个按钮不再并排——整单保存是吸底「未保存修改」条里的主按钮，Excel 粘贴收进「添加商品 ▾」菜单，
  // 原用来区分两者的专用颜色 class 不再需要。
  const detailActionButtonColorFailure = await runTest('详情页整单保存与 Excel 粘贴入口应明确区分', () => {
    const unsavedBarSource = detailSource.slice(
      detailSource.indexOf('<div className="wh-order-detail-unsaved-bar"'),
      detailSource.indexOf('</section>', detailSource.indexOf('<div className="wh-order-detail-unsaved-bar"')),
    )
    assert(
      unsavedBarSource.includes('type="primary"') && unsavedBarSource.includes('onClick={() => void handleSaveEditedLines()}'),
      '整单保存应是吸底未保存修改条里的主按钮',
    )
    assert(
      detailSource.includes("{ key: 'excelPaste', icon: <FileExcelOutlined />, label: t('storeOrders.excelPaste') }") &&
        detailSource.includes("resetPasteState('allocQuantity')\n      setPasteModalOpen(true)"),
      'Excel 粘贴应收进添加商品菜单，打开前仍重置粘贴状态',
    )
    assert(/\.wh-order-detail-unsaved-bar\s*\{[^}]*position:\s*sticky/.test(detailCssSource), '未保存修改条应吸底')
  })
  if (detailActionButtonColorFailure) failures.push(detailActionButtonColorFailure)

  const keyboardNavigationFailure = await runTest('详情页明细输入框应只支持上下方向键和 Enter 移动焦点', () => {
    assert(detailSource.includes('detailInputRefs'), '详情页缺少明细输入框 ref map')
    assert(detailSource.includes('registerDetailInput'), '详情页缺少明细输入框注册函数')
    assert(detailSource.includes('focusDetailInput'), '详情页缺少明细输入框聚焦函数')
    assert(detailSource.includes('handleDetailInputKeyDown'), '详情页缺少键盘导航处理函数')
    assert(!detailKeyboardHandlerSource.includes("'ArrowRight'"), '键盘导航不应再处理 ArrowRight')
    assert(!detailKeyboardHandlerSource.includes("'ArrowLeft'"), '键盘导航不应再处理 ArrowLeft')
    assert(detailKeyboardHandlerSource.includes("'ArrowDown'") && detailKeyboardHandlerSource.includes("'Enter'"), '键盘导航应处理 ArrowDown 和 Enter')
    assert(detailKeyboardHandlerSource.includes("'ArrowUp'"), '键盘导航应处理 ArrowUp')
    assert(!detailKeyboardHandlerSource.includes("field === 'allocQuantity' ? 'importPrice' : 'allocQuantity'"), '左右键不应再在发货数和进口价之间移动')
    assert(!detailKeyboardHandlerSource.includes('nextField'), '上下方向键不应再引入横向目标字段')
    assert(detailKeyboardHandlerSource.includes('event.preventDefault()'), '上下方向键应阻止 InputNumber 默认加减')
    assert(detailKeyboardHandlerSource.includes('if (!nextRow)'), '上下方向键越过首尾行时应安全返回')
    assert(detailKeyboardHandlerSource.includes('focusDetailInput(nextRow.detailGUID, field)'), '上下方向键和 Enter 应保持当前列移动焦点')
    assert(detailSource.includes("focus?.({ cursor: 'all' })"), '方向键切入输入框后应默认全选文本，方便直接覆盖编辑')
    assert(detailMainTableSource.includes('onKeyDown={(event) => handleDetailInputKeyDown(event, record.detailGUID, \'allocQuantity\')}'), '发货数输入框应绑定键盘导航')
    assert(detailMainTableSource.includes('onKeyDown={(event) => handleDetailInputKeyDown(event, record.detailGUID, \'importPrice\')}'), '进口价输入框应绑定键盘导航')
    assert(!detailKeyboardHandlerSource.includes('updateStoreOrderLine') && !detailKeyboardHandlerSource.includes('batchUpdateStoreOrderLines'), '键盘移动不应自动调用保存接口')
  })
  if (keyboardNavigationFailure) failures.push(keyboardNavigationFailure)

  const amountLabelsFailure = await runTest('详情页顶部金额应显示预计销售额、发货金额 ex GST 和 GST 10%', () => {
    assert(detailSource.includes('estimatedSalesAmount'), '详情页缺少预计销售额计算')
    assert(detailSource.includes('gstAmount'), '详情页缺少 GST 10% 计算')
    assert(detailSource.includes('const totalAllocQuantity = useMemo') && detailSource.includes('draftDelta'), '顶部发货数量应按后端总数叠加页面草稿差值')
    assert(detailSource.includes('const totalAllocVolume = useMemo') && detailSource.includes('Number(item.volume) * (Number(editedAllocQuantity)'), '顶部发货体积应按页面草稿差值更新')
    assert(detailSource.includes('draftTotalImportAmount') && detailSource.includes('Number(allocQuantity) * Number(importPrice) - Number(savedAmount)'), '发货金额 ex GST 应按页面草稿金额差值更新')
    assert(detailSource.includes('detail?.totalAllocatedImportAmount') && detailSource.includes('line.allocatedImportAmount'), '发货金额 ex GST 应优先使用发货/发票金额字段')
    assert(detailSource.includes('line.price') && detailSource.includes('line.allocQuantity'), '预计销售额应按零售价和当前发货数计算')
    // 重设计：金额移到「数量与金额」卡（dl 列表、带 $）；预计销售额只按当前已加载的一页求和，有筛选或超过一页时标「仅本页」。
    assert(
      detailSource.includes(": t('storeOrders.orderAmountLabel')}") &&
        detailSource.includes("? t('warehouseUi.storeOrderDetail.estimatedSalesPage')") &&
        detailSource.includes('formatCurrencyAmount(estimatedSalesAmount)'),
      '订单金额位置应改为显示预计销售额，且只覆盖本页时标明本页',
    )
    assert(detailSource.includes("<dt>{t('storeOrders.importAmountLabel')}</dt>") && detailSource.includes('formatCurrencyAmount(draftTotalImportAmount)'), '发货金额 ex GST 应显示草稿总金额')
    assert(detailSource.includes("<dt>{t('storeOrders.gstAmountLabel')}</dt>") && detailSource.includes('formatCurrencyAmount(gstAmount)'), '详情页应新增 GST 10% 显示')
    assert(detailMainTableSource.includes('Number(edited.allocQuantity ?? record.allocQuantity ?? 0) * Number(edited.importPrice ?? record.importPrice ?? 0)'), '明细进口金额应按当前草稿发货数和进口价显示')
    assert(detailMainTableSource.includes("sortOrder: detailColumnSortOrder('allocatedImportAmount')"), '明细发货金额列应按 allocatedImportAmount 发起服务端排序')
    assert(detailMainTableSource.includes('editedAllocQuantity !== undefined') && detailMainTableSource.includes('Number(record.volume) * Number(editedAllocQuantity)'), '明细发货体积应按当前草稿发货数显示')
  })
  if (amountLabelsFailure) failures.push(amountLabelsFailure)

  const packageScriptFailure = await runTest('订货明细标准测试脚本应包含紧凑 UI 约束', () => {
    assert(packageSource.includes('storeOrderCompactUi.logic.test.ts'), 'test:store-order-detail 应接入 storeOrderCompactUi.logic.test.ts')
  })
  if (packageScriptFailure) failures.push(packageScriptFailure)

  const printIsolationFailure = await runTest('本次紧凑样式不应接入打印页面', () => {
    assert(!pickingListSource.includes('./compact.css'), '配货单打印页不应引入页面紧凑样式')
    assert(!invoiceSource.includes('./compact.css'), '发票页不应引入页面紧凑样式')
    assert(!printCssSource.includes('store-order-list-table'), '打印 CSS 不应包含列表页紧凑样式')
    assert(!printCssSource.includes('store-order-detail-table'), '打印 CSS 不应包含详情页紧凑样式')
  })
  if (printIsolationFailure) failures.push(printIsolationFailure)

  if (failures.length > 0) {
    throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  }

  console.log('storeOrderCompactUi.logic.test: ok')
}

await main()
