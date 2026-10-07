import { existsSync, readFileSync } from 'node:fs'
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

function readCssRule(source: string, selector: string) {
  const escapedSelector = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const match = source.match(new RegExp(`${escapedSelector}\\s*\\{([\\s\\S]*?)\\}`))
  return match?.[1] ?? ''
}

function readColumnBlock(source: string, marker: string) {
  const markerPosition = source.indexOf(marker)
  if (markerPosition < 0) {
    return ''
  }

  const blockStart = source.lastIndexOf('    {', markerPosition)
  const nextBlockStart = source.indexOf('    {', markerPosition + marker.length)
  return source.slice(blockStart, nextBlockStart > 0 ? nextBlockStart : source.length)
}

function readNumericValue(source: string, pattern: RegExp) {
  const match = source.match(pattern)
  return match ? Number(match[1]) : Number.NaN
}

const locationsPageFile = path.resolve(process.cwd(), 'src/pages/Warehouse/Locations/index.tsx')
const locationsCssFile = path.resolve(process.cwd(), 'src/pages/Warehouse/Locations/locations.css')
const locationTypesFile = path.resolve(process.cwd(), 'src/types/location.ts')
const locationServiceFile = path.resolve(process.cwd(), 'src/services/locationService.ts')
const zhLocaleFile = path.resolve(process.cwd(), 'src/i18n/locales/zh.json')
const enLocaleFile = path.resolve(process.cwd(), 'src/i18n/locales/en.json')
const packageFile = path.resolve(process.cwd(), 'package.json')

const locationsPageSource = readFileSync(locationsPageFile, 'utf8')
const locationTypesSource = readFileSync(locationTypesFile, 'utf8')
const locationServiceSource = readFileSync(locationServiceFile, 'utf8')
const zhLocaleSource = readFileSync(zhLocaleFile, 'utf8')
const enLocaleSource = readFileSync(enLocaleFile, 'utf8')
const packageSource = readFileSync(packageFile, 'utf8')
const locationsCssSource = existsSync(locationsCssFile) ? readFileSync(locationsCssFile, 'utf8') : ''

async function main() {
  const failures: string[] = []

  const typeFailure = await runTest('仓库标签商品类型应包含商品条码字段', () => {
    assert(locationTypesSource.includes('productBarcode?: string'), 'LocationProduct 缺少 productBarcode 字段')
  })
  if (typeFailure) failures.push(typeFailure)

  const normalizeFailure = await runTest('仓库标签列表应兼容多种商品条码返回字段', () => {
    assert(locationServiceSource.includes('normalizeLocationProduct'), 'locationService 缺少商品 normalize helper')
    assert(locationServiceSource.includes('normalizeLocationItem'), 'locationService 缺少标签 normalize helper')
    assert(locationServiceSource.includes('productBarcode ??'), 'normalize 应优先读取 productBarcode')
    assert(locationServiceSource.includes('ProductBarcode'), 'normalize 应兼容 ProductBarcode')
    assert(locationServiceSource.includes('raw.barcode'), 'normalize 应兼容 barcode')
    assert(locationServiceSource.includes('raw.Barcode'), 'normalize 应兼容 Barcode')
    assert(locationServiceSource.includes('raw.Products'), 'normalize 应兼容 Products 商品数组')
    assert(locationServiceSource.includes('items: (data?.items ?? []).map(normalizeLocationItem)'), '列表返回应统一 normalize')
  })
  if (normalizeFailure) failures.push(normalizeFailure)

  const hqSyncServiceFailure = await runTest('仓库标签服务应顺序增量同步货位和商品货位', () => {
    const serviceFunctionStart = locationServiceSource.indexOf('export async function syncLocationsFromHq')
    assert(serviceFunctionStart >= 0, 'locationService 缺少 syncLocationsFromHq')

    const serviceFunction = locationServiceSource.slice(serviceFunctionStart)
    const locationsEndpointPosition = serviceFunction.indexOf('/api/react/v1/sync/locations-incremental')
    const productLocationsEndpointPosition = serviceFunction.indexOf('/api/react/v1/sync/product-locations-incremental')

    assert(locationsEndpointPosition >= 0, '货位同步应调用 locations-incremental')
    assert(productLocationsEndpointPosition >= 0, '商品货位同步应调用 product-locations-incremental')
    assert(
      locationsEndpointPosition < productLocationsEndpointPosition,
      '商品货位同步必须在货位同步之后执行',
    )
    assert(serviceFunction.includes('locationResult') && serviceFunction.includes('productLocationResult'), '应返回两段同步结果')
  })
  if (hqSyncServiceFailure) failures.push(hqSyncServiceFailure)

  // 重设计：拆散的货号/商品条码/商品名称/图片四列合并为「绑定商品」一列，每个商品一行。
  const pageWiringFailure = await runTest('仓库标签页应挂载页面样式并在绑定商品列逐个显示商品', () => {
    assert(locationsPageSource.includes("import './locations.css'"), '页面应引入页面级 locations.css')
    assert(locationsPageSource.includes('className="wh-locations-table"'), 'Table 缺少页面前缀 class')
    assert(locationsPageSource.includes("title: t('warehouseUi.locations.colProducts')"), '表格缺少绑定商品列')
    assert(
      locationsPageSource.includes('record.products.map((product, index) => renderProductLine(record, product, index))'),
      '绑定商品列应每个商品一行',
    )
    assert(locationsPageSource.includes("t('warehouseUi.locations.emptyLocation')"), '空位应明确显示未绑定商品')
  })
  if (pageWiringFailure) failures.push(pageWiringFailure)

  // HQ → HBweb 的「从HQ更新货位」已于 2026-09-29 停用（后端返回 410），页面不得再提供入口。
  const hqSyncPageFailure = await runTest('仓库标签页不再提供从HQ更新货位入口', () => {
    for (const removed of [
      'syncLocationsFromHq',
      'syncingFromHq',
      'canSyncLocationsFromHq',
      'handleSyncFromHq',
      'CloudSyncOutlined',
      "t('warehouseLocations.syncFromHq'",
      "t('warehouseLocations.syncFromHqSuccessTitle'",
    ]) {
      assert(!locationsPageSource.includes(removed), `页面不应再包含 HQ 同步入口代码：${removed}`)
    }
    assert(
      locationsPageSource.includes('<Button type="primary" icon={<PlusOutlined />} onClick={handleCreate}>'),
      '页头应保留新建货位按钮',
    )
  })
  if (hqSyncPageFailure) failures.push(hqSyncPageFailure)

  const barcodeColumnFailure = await runTest('商品行应显示货号、商品条码与名称，条码缺失不回退货号', () => {
    const lineStart = locationsPageSource.indexOf('const renderProductLine = (')
    assert(lineStart >= 0, '缺少商品行渲染函数')
    const lineSource = locationsPageSource.slice(lineStart, locationsPageSource.indexOf('const columns: ColumnsType<LocationItem>', lineStart))
    assert(
      lineSource.includes('{product.productBarcode ? <span className="wh-locations-sub">{product.productBarcode}</span> : null}'),
      '商品条码只读取 productBarcode，缺失时不显示',
    )
    assert(!/productBarcode\s*\|\|\s*product\.itemNumber/.test(lineSource), '商品条码缺失时不能回退显示货号')
    assert(lineSource.includes('copyable={product.itemNumber ? { text: product.itemNumber } : false}'), '货号应保留复制能力')
    assert(lineSource.includes('className="wh-locations-product-name"'), '商品名称应单行省略展示')
    assert(lineSource.includes('<Image src={product.productImage} width={24} height={24}'), '商品缩略图 24px 且可预览')
  })
  if (barcodeColumnFailure) failures.push(barcodeColumnFailure)

  const locationBarcodeFailure = await runTest('货位列应完整显示货位代码与条码，条码图收进预览弹层', () => {
    const locationCodeColumn = readColumnBlock(locationsPageSource, "dataIndex: 'locationCode'")
    const locationCodePosition = locationsPageSource.indexOf("dataIndex: 'locationCode'")
    const locationTypePosition = locationsPageSource.indexOf("dataIndex: 'locationType'")

    assert(locationCodeColumn.includes("title: t('warehouseUi.locations.colLocation')"), '首列应为货位')
    assert(locationsPageSource.includes('<div className="wh-locations-sub">{record.locationBarcode || \'--\'}</div>'), '货位条码文字应显示在代码下方')
    assert(
      locationsPageSource.includes('<BarcodePreview value={record.locationCode} align="left" compactCopy textNoWrap />') &&
        locationsPageSource.includes('<BarcodePreview value={record.locationBarcode} align="left" compactCopy textNoWrap />'),
      '预览弹层应同时提供货位代码与货位条码的条码图和复制',
    )
    assert(!locationsPageSource.includes('textMaxWidth'), '货位代码与条码不应通过 textMaxWidth 省略隐藏')
    assert(locationCodePosition < locationTypePosition, '类型列应放在货位列之后')
  })
  if (locationBarcodeFailure) failures.push(locationBarcodeFailure)

  const sortingFailure = await runTest('仓库标签基础列应使用服务端远程排序', () => {
    // 重设计后货位条码、更新人并入货位列和更新列，不再单独排序；使用状态排序挂在绑定商品列。
    const sortableMarkers = [
      "dataIndex: 'locationCode'",
      "dataIndex: 'locationType'",
      "dataIndex: 'status'",
      "key: 'products'",
      "dataIndex: 'updatedAt'",
    ]

    for (const marker of sortableMarkers) {
      const column = readColumnBlock(locationsPageSource, marker)
      assert(column.includes('sorter: true'), `${marker} 缺少 sorter: true`)
      assert(column.includes('sortOrder:'), `${marker} 缺少受控 sortOrder`)
    }

    const unsortableMarkers = [
      "key: 'action'",
    ]

    for (const marker of unsortableMarkers) {
      const column = readColumnBlock(locationsPageSource, marker)
      assert(!column.includes('sorter: true'), `${marker} 不应开启排序`)
      assert(!column.includes('sortOrder:'), `${marker} 不应绑定排序状态`)
    }

    assert(locationsPageSource.includes('LOCATION_SORT_FIELD_MAP'), '页面缺少远程排序字段白名单')
    assert(locationsPageSource.includes("products: 'Usage'"), '使用状态排序应映射到 Usage')
    assert(locationsPageSource.includes("const [sortBy, setSortBy] = useState<LocationSortBy>(DEFAULT_LOCATION_SORT_BY)"), '页面缺少 sortBy 状态')
    assert(locationsPageSource.includes("const [sortOrder, setSortOrder] = useState<SortOrder>(DEFAULT_LOCATION_SORT_ORDER)"), '页面缺少 sortOrder 状态')
    assert(locationsPageSource.includes('sortDirection: toApiSortDirection(effectiveSortOrder)'), '列表请求应发送排序方向')
    assert(locationsPageSource.includes('onChange={handleTableChange}'), '表格应使用统一排序分页回调')
    assert(locationsPageSource.includes("extra.action === 'sort'"), '排序回调应识别 sort action')
    assert(locationsPageSource.includes('pagination.pageSize || pageSize'), '排序变更应沿用当前页容量')
    assert(locationsPageSource.includes('nextSortBy') && locationsPageSource.includes('nextSortOrder'), '排序回调应传递服务端排序字段和方向')
    assert(locationServiceSource.includes('SortDirection: params.sortDirection'), 'locationService 应发送后端 DTO 的 SortDirection 字段')
    assert(!locationServiceSource.includes('sortDirection: params.sortDirection'), 'locationService 不应发送小写 sortDirection 字段')
  })
  if (sortingFailure) failures.push(sortingFailure)

  const cssFailure = await runTest('货位页样式应使用页面前缀类名并保证关键字段可读', () => {
    const codeRule = readCssRule(locationsCssSource, '.wh-locations-code')
    const subRule = readCssRule(locationsCssSource, '.wh-locations-sub')
    const productNameRule = readCssRule(locationsCssSource, '.wh-locations-product-name')
    const selectors = locationsCssSource.match(/^[^\s/@}][^{]*\{/gm) ?? []

    assert(selectors.length > 0, '应能读取到 CSS 规则')
    for (const selector of selectors) {
      assert(selector.trim().startsWith('.wh-locations-'), `选择器必须带页面前缀：${selector.trim()}`)
    }
    assert(/white-space:\s*nowrap/.test(codeRule), '货位代码应保持单行完整显示')
    assert(!/overflow:\s*hidden/.test(codeRule), '货位代码不应被隐藏截断')
    assert(/font-variant-numeric:\s*tabular-nums/.test(subRule), '条码、时间等数字应等宽对齐')
    assert(/color:\s*#667085/.test(subRule), '次要文字颜色不应比 #667085 更浅')
    assert(/text-overflow:\s*ellipsis/.test(productNameRule) && /min-width:\s*0/.test(productNameRule), '商品名称应单行省略且允许收缩')
    for (const state of ['full', 'partial', 'empty', 'off']) {
      assert(readCssRule(locationsCssSource, `.wh-locations-rack-${state}`).includes('background'), `货架格缺少 ${state} 状态样式`)
    }
  })
  if (cssFailure) failures.push(cssFailure)

  const layoutFailure = await runTest('货位表格去掉序号列，横向滚动预算包含选择列', () => {
    assert(!locationsPageSource.includes("key: 'index'") && !locationsPageSource.includes("t('column.index')"), '不应再有序号列')
    const productsColumn = readColumnBlock(locationsPageSource, "key: 'products'")
    const actionColumn = readColumnBlock(locationsPageSource, "key: 'action'")
    assert(readNumericValue(productsColumn, /width:\s*(\d+)/) >= 380, '绑定商品列应保留至少 380 宽度，商品名称才不至于过早省略')
    assert(readNumericValue(actionColumn, /width:\s*(\d+)/) <= 80, '操作列收敛为「编辑 + ⋯」后应压到 80 以内')
    assert(!locationsPageSource.includes('virtual'), '行高随商品数变化，不再使用固定行高的虚拟滚动')
    assert(locationsPageSource.includes('columnWidth: 40'), 'rowSelection 应固定选择列宽度')
    assert(
      locationsPageSource.includes('const selectionColumnWidth = canManageLocations ? 40 : 0'),
      'scroll.x 选择列预算应按货位管理权限为 40 或 0',
    )
    assert(locationsPageSource.includes('selectionColumnWidth + columns.reduce'), 'scroll.x 应包含当前可见选择列和全部业务列宽度预算')
    assert(locationsPageSource.includes('scroll={{ x: tableScrollX }}'), 'Table 应使用包含选择列预算的动态 scroll.x')
    assert(
      locationsPageSource.includes("showTotal: (value) => t('warehouseUi.locations.paginationTotal', { count: formatCount(value) })"),
      '分页应显示货位总数',
    )
  })
  if (layoutFailure) failures.push(layoutFailure)

  const batchUnbindFailure = await runTest('仓库标签页应按所选货位批量解绑有效商品关联', () => {
    assert(locationsPageSource.includes('batchUnbindLocationProducts'), '页面应复用批量解绑服务')
    assert(locationsPageSource.includes('const [selectedRowKeys, setSelectedRowKeys] = useState'), '页面缺少受控行选择状态')
    assert(locationsPageSource.includes('const [batchUnbinding, setBatchUnbinding] = useState(false)'), '页面缺少批量解绑 loading 状态')
    assert(locationsPageSource.includes('selectedBindings'), '页面应从所选货位展开商品关联')
    assert(locationsPageSource.includes('buildSelectedLocationProductBindings'), '页面应复用纯函数展开并去重商品关联')
    assert(locationsPageSource.includes('hasUnbindableProducts'), '页面应复用纯函数判断货位能否解绑')
    assert(locationsPageSource.includes('coordinateBatchUnbindLocationProducts'), '页面应复用异步协调函数管理解绑和刷新状态流')
    assert(locationsPageSource.includes('const rowSelection'), '页面应配置 Table rowSelection')
    assert(locationsPageSource.includes('selectedRowKeys,'), 'rowSelection 应受控')
    assert(locationsPageSource.includes('getCheckboxProps:'), 'rowSelection 应配置空货位禁选')
    assert(locationsPageSource.includes('disabled: !hasUnbindableProducts(record)'), '没有有效 productCode 的货位应禁选')
    assert(locationsPageSource.includes('rowSelection={access.canManageWarehouseLocations ? rowSelection : undefined}'), '仅货位管理权限用户可选择批量解绑货位')
    assert(locationsPageSource.includes('setSelectedRowKeys((currentKeys)'), '列表刷新后应清理当前页不存在的选择')
    assert(locationsPageSource.includes('danger'), '批量解绑按钮应使用危险操作样式')
    // 重设计：批量解绑收进勾选后才出现的操作条，按钮文案改为「解绑全部商品…」，确认与结果提示沿用原文案。
    assert(locationsPageSource.includes("t('warehouseUi.locations.unbindAll')"), '批量按钮应使用国际化文案')
    assert(
      locationsPageSource.includes('<SelectionActionBar selectedCount={selectedLocations.length}') &&
        locationsPageSource.includes("t('warehouseUi.locations.selectionLinks', { count: selectedBindings.length })"),
      '操作条应展示货位数和商品关联数',
    )
    assert(locationsPageSource.includes('disabled={!selectedBindings.length || loading || batchUnbinding}'), '按钮应在无选择、加载或执行中禁用')
    assert(locationsPageSource.includes('loading={batchUnbinding}'), '按钮应绑定批量解绑 loading')
    assert(locationsPageSource.includes('const handleBatchUnbind = () => {'), '页面缺少批量解绑处理函数')
    assert(locationsPageSource.includes('Modal.confirm({'), '批量解绑应二次确认')
    assert(locationsPageSource.includes("t('warehouseLocations.batchUnbindContent'"), '确认正文应说明数量和不可恢复')
    assert(locationsPageSource.includes('setBatchUnbinding(true)'), '确认后应进入执行状态')
    assert(locationsPageSource.includes('unbind: batchUnbindLocationProducts'), '异步协调函数应调用批量解绑服务')
    assert(locationsPageSource.includes("message.success(t('warehouseLocations.batchUnbindSuccess'"), '全成功应展示 success')
    assert(locationsPageSource.includes("message.warning(t('warehouseLocations.batchUnbindPartialFailed'"), '部分失败应展示 warning')
    assert(locationsPageSource.includes("message.error(t('warehouseLocations.batchUnbindFailed'"), '全失败应展示 error')
    assert(
      locationsPageSource.includes('const outcome = await loadData()') &&
        locationsPageSource.includes("return outcome.status === 'success' ? outcome.items : undefined"),
      '异步协调函数应注入当前列表刷新，刷新失败或被取代时返回 undefined',
    )
    assert(locationsPageSource.includes('shouldApplyPatchedData'), '页面应按协调结果判断是否写回本地补丁')
    assert(
      locationsPageSource.includes("if (shouldApplyPatchedData && patchedData && refreshOutcome !== 'superseded') {") &&
        locationsPageSource.includes('setData(patchedData)'),
      '刷新失败时应写入剔除成功关联后的本地数据；被更新请求取代时不能覆盖新数据',
    )
  })
  if (batchUnbindFailure) failures.push(batchUnbindFailure)

  const singleUnbindFailure = await runTest('单个商品解绑应二次确认、与批量解绑同权限并复用同一接口', () => {
    const start = locationsPageSource.indexOf('const confirmUnbindProduct = (record: LocationItem, product: LocationProduct) => {')
    assert(start >= 0, '缺少单个商品解绑函数')
    const section = locationsPageSource.slice(start, locationsPageSource.indexOf('const selectedLocationGuidSet', start))
    assert(section.includes('Modal.confirm({') && section.includes("t('warehouseUi.locations.unbindContent')"), '单个解绑应二次确认并说明不可恢复')
    assert(section.includes('okButtonProps: { danger: true }'), '确认按钮应为危险样式')
    assert(
      section.includes('batchUnbindLocationProducts([{ locationGuid: record.locationGuid, productCode }])'),
      '应复用 DELETE /locations/{guid}/products/{productCode}，只发送这一条关联',
    )
    assert(section.includes('refreshSummaries()') && section.includes('void loadData()'), '成功后应刷新列表与计数')
    assert(section.includes("message.error(t('warehouseUi.locations.unbindFailed'"), '失败应提示原因')
    assert(locationsPageSource.includes('{canManageLocations && productCode ? ('), '单个解绑按钮应与批量解绑同为货位管理权限')
  })
  if (singleUnbindFailure) failures.push(singleUnbindFailure)

  const raceGuardFailure = await runTest('货位列表请求应受最新请求守卫保护，卸载时作废', () => {
    assert(locationsPageSource.includes('const listRequestGuardRef = useRef(createLatestRequestGuard())'), '列表缺少独立 guard')
    assert(locationsPageSource.includes('listRequestGuardRef.current,') && locationsPageSource.includes('runLatestGuardedRequest('), '列表请求未接入 guarded request')
    assert(
      locationsPageSource.includes('listRequestGuardRef.current.invalidate()') &&
        locationsPageSource.includes('distributionRequestGuardRef.current.invalidate()') &&
        locationsPageSource.includes('window.clearTimeout(keywordTimerRef.current)'),
      '卸载时应作废列表与分布请求并清理关键字防抖',
    )
    assert(
      locationsPageSource.includes('window.setTimeout(() => latestApplyFiltersRef.current({ keyword: value.trim() }), 300)'),
      '关键字应防抖约 300ms 并通过最新入口生效',
    )
  })
  if (raceGuardFailure) failures.push(raceGuardFailure)

  const localeAndScriptFailure = await runTest('商品条码文案和测试脚本应接入项目', () => {
    assert(zhLocaleSource.includes('"productBarcode": "商品条码"'), '中文列名缺少商品条码')
    assert(enLocaleSource.includes('"productBarcode": "Product Barcode"'), '英文列名缺少 Product Barcode')
    assert(zhLocaleSource.includes('"syncFromHq": "从HQ更新货位"'), '中文文案缺少从HQ更新货位')
    assert(enLocaleSource.includes('"syncFromHq": "Update Locations from HQ"'), '英文文案缺少 Update Locations from HQ')
    assert(zhLocaleSource.includes('"syncResultStats": "新增 {{added}}，更新 {{updated}}，错误 {{errors}}"'), '中文文案缺少同步结果统计')
    assert(enLocaleSource.includes('"syncResultStats": "Added {{added}}, updated {{updated}}, errors {{errors}}"'), '英文文案缺少同步结果统计')
    assert(packageSource.includes('"test:warehouse-locations"'), 'package.json 缺少 test:warehouse-locations 脚本')
    assert(packageSource.includes('warehouseLocationsCompactUi.logic.test.ts'), '测试脚本未运行仓库标签紧凑 UI 约束')
    assert(packageSource.includes('locationService.hqSync.test.ts'), '测试脚本未运行仓库标签 HQ 同步服务行为测试')

    const expectedZhCopy = [
      '"batchUnbind": "批量解绑"',
      '"selectedLocations": "已选 {{locations}} 个货位，共 {{products}} 个商品关联"',
      '"batchUnbindTitle": "批量解绑商品关联"',
      '"batchUnbindContent": "将解绑所选 {{locations}} 个货位中的 {{products}} 个商品关联，此操作不可恢复。"',
      '"batchUnbindConfirm": "确认解绑"',
      '"batchUnbindSuccess": "已成功解绑 {{succeeded}} 个商品关联"',
      '"batchUnbindPartialFailed": "成功解绑 {{succeeded}} 个商品关联，{{failed}} 个失败"',
      '"batchUnbindFailed": "批量解绑失败，共 {{failed}} 个商品关联未解绑"',
    ]
    const expectedEnCopy = [
      '"batchUnbind": "Batch Unbind"',
      '"selectedLocations": "Selected locations: {{locations}}; product links: {{products}}"',
      '"batchUnbindTitle": "Batch Unbind Product Links"',
      '"batchUnbindContent": "Selected locations: {{locations}}. Product links to unbind: {{products}}. This action cannot be undone."',
      '"batchUnbindConfirm": "Unbind"',
      '"batchUnbindSuccess": "Product links unbound: {{succeeded}}"',
      '"batchUnbindPartialFailed": "Product links unbound: {{succeeded}}. Failed: {{failed}}"',
      '"batchUnbindFailed": "No product links were unbound. Failed: {{failed}}"',
    ]

    for (const copy of expectedZhCopy) {
      assert(zhLocaleSource.includes(copy), `中文批量解绑文案缺失: ${copy}`)
    }
    for (const copy of expectedEnCopy) {
      assert(enLocaleSource.includes(copy), `英文批量解绑文案缺失: ${copy}`)
    }
    assert(!expectedZhCopy.some((copy) => copy.includes('—') || copy.includes('–')), '中文批量解绑文案不能使用破折号')
    assert(!expectedEnCopy.some((copy) => copy.includes('—') || copy.includes('–')), '英文批量解绑文案不能使用破折号')
  })
  if (localeAndScriptFailure) failures.push(localeAndScriptFailure)

  if (failures.length > 0) {
    throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  }

  console.log('warehouseLocationsCompactUi.logic.test: ok')
}

await main()
