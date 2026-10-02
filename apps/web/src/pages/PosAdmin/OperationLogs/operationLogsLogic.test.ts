import { readFileSync } from 'node:fs'
import {
  buildOperationAuditQuery,
  buildSystemLogLink,
  DEFAULT_OPERATION_AUDIT_SORT,
  createLatestOperationAuditRequestGuard,
  formatMoney,
  formatSignedMoney,
  OPERATION_AUDIT_SORT_FIELDS,
  resolveOperationAuditTableChange,
  summarizeProducts,
  summarizeProductName,
  primaryItemNumberOf,
  toLegacyEmployeeSummary,
} from './operationLogsLogic'
import * as operationLogsLogic from './operationLogsLogic'

function assertEqual(actual: unknown, expected: unknown, label: string) {
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

assertDeepEqual(
  buildOperationAuditQuery({
    startUtc: '2026-07-01T00:00:00.000Z',
    endUtc: '2026-07-08T00:00:00.000Z',
    storeCode: ' S01 ',
    cashierKeyword: ' Amy ',
    deviceCode: ' POS-01 ',
    deviceSystem: ' iPadOS ',
    operationType: 'SALE_COMPLETE',
    outcome: 'Succeeded',
    productKeyword: ' 10001 ',
    orderGuid: ' order-1 ',
    keyword: ' trace ',
    page: 2,
    pageSize: 50,
    sortBy: 'amountDelta',
    sortOrder: 'asc',
  }),
  {
    fromUtc: '2026-07-01T00:00:00.000Z',
    toUtc: '2026-07-08T00:00:00.000Z',
    storeCode: 'S01',
    cashierKeyword: 'Amy',
    deviceCode: 'POS-01',
    deviceSystem: 'iPadOS',
    operationType: 'SALE_COMPLETE',
    outcome: 'Succeeded',
    productKeyword: '10001',
    orderGuid: 'order-1',
    keyword: 'trace',
    pageNumber: 2,
    pageSize: 50,
    sortBy: 'amountDelta',
    sortOrder: 'asc',
  },
  '查询参数应裁剪文本并映射分页字段',
)

assertDeepEqual(
  OPERATION_AUDIT_SORT_FIELDS,
  ['occurredAtUtc', 'storeCode', 'operationType', 'amountDelta', 'deviceCode', 'outcome'],
  '员工操作日志仅允许六个服务端排序字段',
)
assertDeepEqual(
  DEFAULT_OPERATION_AUDIT_SORT,
  { sortBy: 'occurredAtUtc', sortOrder: 'descend' },
  '员工操作日志默认按发生时间倒序',
)
assertDeepEqual(
  resolveOperationAuditTableChange(
    { page: 4, pageSize: 20, sortBy: 'occurredAtUtc', sortOrder: 'descend' },
    { action: 'sort', page: 4, pageSize: 20, sortBy: 'storeCode', sortOrder: 'ascend' },
  ),
  { page: 1, pageSize: 20, sortBy: 'storeCode', sortOrder: 'ascend' },
  '切换排序时应回到第一页并采用表头排序状态',
)
assertDeepEqual(
  resolveOperationAuditTableChange(
    { page: 1, pageSize: 20, sortBy: 'storeCode', sortOrder: 'ascend' },
    { action: 'paginate', page: 3, pageSize: 50 },
  ),
  { page: 3, pageSize: 50, sortBy: 'storeCode', sortOrder: 'ascend' },
  '分页时应保留当前排序状态',
)

assertEqual(formatSignedMoney(12.3, 'AUD'), '+$12.30', '正金额应显示加号')
assertEqual(formatSignedMoney(-5, 'AUD'), '-$5.00', '负金额应保留负号')
assertEqual(formatSignedMoney(null, 'AUD'), '-', '缺少金额应显示占位符')
assertEqual(formatMoney(12.3, 'AUD'), '$12.30', '普通金额应显示 AUD 符号和两位小数')
assertEqual(formatMoney(-5, 'AUD'), '-$5.00', '普通金额应保留负号')

assertEqual(summarizeProducts(null), '-', '没有商品时应显示占位符')
assertEqual(
  summarizeProducts({ productCount: 3, primaryProductName: 'Milk' }),
  'Milk +2',
  '多商品应显示首个商品和剩余数量',
)
assertEqual(
  summarizeProducts({ productCount: 2, primaryProduct: 'Halloween Napkins', primaryItemNumber: ' XH0001640 ' }),
  'Halloween Napkins (XH0001640) +1',
  '有货号时应紧跟主商品名显示，剩余数量放最后',
)
assertEqual(
  summarizeProducts({ productCount: 1, primaryProduct: 'XH0001640', primaryItemNumber: 'XH0001640' }),
  'XH0001640',
  '商品名与货号相同时不应重复显示',
)
assertEqual(
  summarizeProductName({ productCount: 3, primaryProduct: 'Halloween Napkins', primaryItemNumber: 'XH0001640' }),
  'Halloween Napkins +2',
  '图文摘要的名称行不含货号（货号单独一行）',
)
assertEqual(primaryItemNumberOf({ productCount: 1, primaryProduct: 'Napkins', primaryItemNumber: ' XH0001640 ' }), 'XH0001640', '货号去除首尾空白')
assertEqual(primaryItemNumberOf({ productCount: 1, primaryProduct: 'XH1', primaryItemNumber: 'XH1' }), undefined, '货号与名称相同时不单独显示')
assertEqual(primaryItemNumberOf({ productCount: 0, primaryItemNumber: 'XH1' }), undefined, '没有商品时不显示货号')
assertEqual(
  summarizeProducts({ productCount: 1 }, '商品'),
  '商品',
  '缺少商品摘要时应使用当前语言兜底',
)

assertEqual(
  buildSystemLogLink({
    deviceCode: 'POS 01',
    deviceSystem: 'Windows',
    traceId: 'trace/1',
    occurredAtUtc: '2026-07-10T01:02:03.000Z',
  }),
  '/system/center-logs?projectCode=hbpos_win&deviceCode=POS+01&traceId=trace%2F1&fromUtc=2026-07-10T00%3A57%3A03.000Z&toUtc=2026-07-10T01%3A07%3A03.000Z',
  '系统日志跳转应携带项目、设备、Trace 和前后五分钟窗口',
)

assertEqual(
  buildSystemLogLink({
    deviceCode: 'IPAD-01',
    deviceSystem: 'iPadOS',
    traceId: 'trace-ipad',
    occurredAtUtc: '2026-07-10T01:02:03.000Z',
  }),
  '/system/center-logs?projectCode=hbpos_ipad&deviceCode=IPAD-01&traceId=trace-ipad&fromUtc=2026-07-10T00%3A57%3A03.000Z&toUtc=2026-07-10T01%3A07%3A03.000Z',
  'iPad 员工日志跳转应限定到 iPad 程序日志项目',
)

assertEqual(
  buildSystemLogLink({
    deviceCode: 'POS-UNKNOWN',
    deviceSystem: null,
    traceId: 'trace-unknown',
    occurredAtUtc: '2026-07-10T01:02:03.000Z',
  }),
  '/system/center-logs?traceId=trace-unknown&fromUtc=2026-07-10T00%3A57%3A03.000Z&toUtc=2026-07-10T01%3A07%3A03.000Z',
  '未知平台的员工日志跳转只能使用 Trace，不能错误指定项目或终端',
)

assertEqual(
  buildSystemLogLink({
    deviceSystem: 'Unknown',
    occurredAtUtc: '2026-07-10T01:02:03.000Z',
  }),
  undefined,
  '未知平台且没有 Trace 时不应提供可能错误的系统日志跳转',
)

assertEqual(
  typeof (operationLogsLogic as Record<string, unknown>).OPERATION_TYPE_KEYS,
  'object',
  '操作日志应提供固定事件代码映射',
)

const operationTypeKeys = (
  operationLogsLogic as unknown as { OPERATION_TYPE_KEYS: Record<string, string> }
).OPERATION_TYPE_KEYS
assertDeepEqual(
  Object.keys(operationTypeKeys),
  [
    'CASHIER_LOGIN',
    'CASHIER_LOGOUT',
    'CART_ITEM_ADD',
    'CART_ITEM_REMOVE',
    'CART_ITEM_QUANTITY_CHANGE',
    'CART_ITEM_PRICE_CHANGE',
    'CART_LINE_DISCOUNT_CHANGE',
    'CART_ORDER_DISCOUNT_CHANGE',
    'CART_CLEAR',
    'ORDER_HOLD',
    'ORDER_RECALL',
    'ORDER_CANCEL',
    'CASH_DRAWER_OPEN',
    'PAYMENT_TENDER_ADD',
    'PAYMENT_TENDER_REMOVE',
    'PAYMENT_CANCEL',
    'SALE_COMPLETE',
    'RETURN_REFUND_COMPLETE',
    'SALE_VOID',
    'RECEIPT_REPRINT',
    'INSTALLMENT_REPAYMENT_COMPLETE',
    'INSTALLMENT_REPAYMENT_CANCEL',
    'DAILY_CLOSE_SAVE',
    'DAILY_CLOSE_REPRINT',
    'LINKLY_SETTLEMENT',
    'LINKLY_SETTLEMENT_REPRINT',
    'CARD_PAYMENT_SUPERVISOR_RESOLUTION',
    'PERMISSION_OVERRIDE',
    'INSTALLMENT_PICKUP_CONFIRM',
    'CATALOG_RESET',
    'TEST_SALES_DATA_RESET',
    'DEVICE_REREGISTER',
    'API_SERVER_CHANGE',
    'REMOTE_MAINTENANCE_INSTALL',
  ],
  '固定事件代码应完整覆盖核心收银链路',
)
assertEqual(
  operationTypeKeys.LINKLY_SETTLEMENT,
  'operationLogs.operations.linklySettlement',
  'Linkly 结算应映射到固定的本地化键',
)
assertEqual(
  operationTypeKeys.CARD_PAYMENT_SUPERVISOR_RESOLUTION,
  'operationLogs.operations.cardPaymentSupervisorResolution',
  '主管付款结案应映射到固定的本地化键',
)

assertEqual(
  typeof (operationLogsLogic as Record<string, unknown>).normalizeOperationAuditPage,
  'function',
  '操作日志应兼容两种分页字段命名',
)
assertDeepEqual(
  (
    operationLogsLogic as unknown as {
      normalizeOperationAuditPage: (payload: unknown) => unknown
    }
  ).normalizeOperationAuditPage({ items: [{ eventId: 'event-1' }], totalCount: 1, pageIndex: 3, pageSize: 50 }),
  { items: [{ eventId: 'event-1' }], total: 1, pageNumber: 3, pageSize: 50 },
  '分页响应应统一为前端稳定结构',
)

const operationLogsPageSource = readFileSync('src/pages/PosAdmin/OperationLogs/index.tsx', 'utf8')
// 详情（商品明细、会话标记、平台）在独立的详情面板里，与员工操作日志合并页一起改版。
const detailPanelSource = readFileSync('src/pages/PosAdmin/OperationLogs/PosLogDetailPanel.tsx', 'utf8')
const zhLocale = JSON.parse(readFileSync('src/i18n/locales/zh.json', 'utf8'))
const enLocale = JSON.parse(readFileSync('src/i18n/locales/en.json', 'utf8'))
// 每个事件代码都必须有中英文案，否则页面和筛选下拉会直接显示原始代码。
for (const [operationType, i18nKey] of Object.entries(operationTypeKeys)) {
  const leafKey = i18nKey.replace('operationLogs.operations.', '')
  assertEqual(
    typeof zhLocale.operationLogs.operations[leafKey],
    'string',
    `${operationType} 应提供中文文案`,
  )
  assertEqual(
    typeof enLocale.operationLogs.operations[leafKey],
    'string',
    `${operationType} 应提供英文文案`,
  )
}
assertEqual(
  zhLocale.operationLogs.operations.apiServerChange,
  '切换服务器地址',
  '切换服务器地址应提供中文文案',
)
assertEqual(
  zhLocale.operationLogs.operations.linklySettlement,
  'Linkly 结算',
  'Linkly 结算应提供中文文案',
)
assertEqual(
  enLocale.operationLogs.operations.linklySettlement,
  'Linkly Settlement',
  'Linkly 结算应提供英文文案',
)
assertEqual(
  zhLocale.operationLogs.operations.cardPaymentSupervisorResolution,
  '主管付款结案',
  '主管付款结案应提供中文文案',
)
assertEqual(
  enLocale.operationLogs.operations.cardPaymentSupervisorResolution,
  'Card Payment Supervisor Resolution',
  '主管付款结案应提供英文文案',
)
assertEqual(
  zhLocale.operationLogs.platforms.iPadOS,
  'iPadOS',
  'iPadOS 应提供中文平台文案',
)
assertEqual(
  enLocale.operationLogs.platforms.Unknown,
  'Unknown',
  'Unknown 应提供英文平台文案',
)
assertEqual(
  operationLogsPageSource.includes('<PosProductSummary record={record}') && detailPanelSource.includes('<PosProductSummary'),
  true,
  '列表商品列与详情标题应使用带图片和货号的商品摘要',
)
assertEqual(
  detailPanelSource.includes('formatMoney(item.beforeUnitPrice'),
  true,
  '商品详情单价应使用金额格式化',
)
assertEqual(
  detailPanelSource.includes('formatSignedMoney(item.actualAmountDelta'),
  true,
  '商品详情金额变化应显示正负号',
)
assertEqual(
  detailPanelSource.includes('item.itemNumber') && detailPanelSource.includes('item.lineKind'),
  true,
  '商品详情应显示货号和行类型',
)
assertEqual(
  detailPanelSource.includes('record.isOfflineCached') &&
    detailPanelSource.includes('record.isEmergencyOverride'),
  true,
  '员工详情应显示离线缓存和紧急授权快照',
)
assertEqual(
  operationLogsPageSource.includes("name=\"deviceSystem\"") &&
    operationLogsPageSource.includes("key: 'deviceSystem'") &&
    detailPanelSource.includes('record.deviceSystem'),
  true,
  '操作日志应支持平台筛选、列表列和详情展示',
)
assertEqual(
  operationLogsPageSource.includes('KeyboardSensor') &&
    operationLogsPageSource.includes('sortableKeyboardCoordinates') &&
    operationLogsPageSource.includes('HolderOutlined'),
  true,
  '主表列拖拽应提供专用把手并支持键盘传感器',
)
assertEqual(
  operationLogsPageSource.includes('onKeyDown={(event) =>') &&
    operationLogsPageSource.includes('dispatchOperationLogDragHandleKeyDown'),
  true,
  '拖拽把手应显式组合 dnd 键盘监听并阻止事件冒泡',
)
assertEqual(
  operationLogsPageSource.includes("overflowWrap: 'anywhere'") &&
    operationLogsPageSource.includes("whiteSpace: 'normal'"),
  true,
  '终端编号等连续长文本应在表格单元格内自动换行',
)
const deviceColumnSource = operationLogsPageSource.slice(
  operationLogsPageSource.indexOf("title: t('operationLogs.columns.device')"),
  operationLogsPageSource.indexOf("title: t('operationLogs.columns.outcome')"),
)
assertEqual(
  deviceColumnSource.includes('style={WRAPPED_TABLE_CELL_STYLE}') &&
    !deviceColumnSource.includes('ellipsis:'),
  true,
  '终端列应使用自动换行样式且不再截断内容',
)
assertEqual(
  zhLocale.operationLogs.dragColumn,
  '拖动调整列顺序：{{column}}',
  '中文拖拽把手标签应包含列名占位符',
)
assertEqual(
  enLocale.operationLogs.dragColumn,
  'Drag to reorder column: {{column}}',
  '英文拖拽把手标签应包含列名占位符',
)
assertEqual(
  typeof zhLocale.operationLogs.dnd.instructions === 'string' &&
    zhLocale.operationLogs.dnd.dragOver.includes('{{column}}') &&
    zhLocale.operationLogs.dnd.dragOver.includes('{{overColumn}}'),
  true,
  '中文读屏配置应包含键盘说明和本地化源列/目标列占位符',
)
assertEqual(
  typeof enLocale.operationLogs.dnd.instructions === 'string' &&
    enLocale.operationLogs.dnd.dragOver.includes('{{column}}') &&
    enLocale.operationLogs.dnd.dragOver.includes('{{overColumn}}'),
  true,
  '英文读屏配置应包含键盘说明和本地化源列/目标列占位符',
)
assertEqual(
  operationLogsPageSource.includes("t('operationLogs.dragColumn', { column: String(column.title) })"),
  true,
  '每个业务列应使用自身本地化标题生成拖拽把手标签',
)
assertEqual(
  operationLogsPageSource.includes("<div style={{ display: 'inline-flex'") &&
    operationLogsPageSource.includes('<div style={{ minWidth: 0 }}>{children}</div>') &&
    !operationLogsPageSource.includes('<span style={{ minWidth: 0 }}>{children}</span>'),
  true,
  '可拖拽表头应使用 div 容纳 AntD sorter，避免 span 包含 div 的无效 DOM',
)
assertEqual(
  operationLogsPageSource.includes('requestGuardRef.current.begin()') &&
    (operationLogsPageSource.match(/requestGuardRef\.current\.isLatest\(requestId\)/g)?.length ?? 0) >= 3,
  true,
  '页面数据、错误与 loading 更新都应受最新请求序号保护',
)
assertEqual(
  operationLogsPageSource.includes('accessibility={dndAccessibility}') &&
    operationLogsPageSource.includes('const dndAccessibility = useMemo('),
  true,
  'DndContext 应使用稳定的本地化读屏说明和播报配置',
)
assertEqual(
  operationLogsPageSource.includes("key: 'actions'") &&
    operationLogsPageSource.includes("fixed: 'right'"),
  true,
  '操作列应固定在右侧且不进入业务列拖拽顺序',
)

function createDeferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: unknown) => void
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise
    reject = rejectPromise
  })
  return { promise, resolve, reject }
}

const requestGuard = createLatestOperationAuditRequestGuard()
const requestState = { data: '', loadError: false, loading: false }
async function settleRequest(requestId: number, request: Promise<string>) {
  try {
    const result = await request
    if (requestGuard.isLatest(requestId)) requestState.data = result
  } catch {
    if (requestGuard.isLatest(requestId)) requestState.loadError = true
  } finally {
    if (requestGuard.isLatest(requestId)) requestState.loading = false
  }
}

const olderRequest = createDeferred<string>()
requestState.loading = true
const olderTask = settleRequest(requestGuard.begin(), olderRequest.promise)
const latestRequest = createDeferred<string>()
requestState.loading = true
const latestTask = settleRequest(requestGuard.begin(), latestRequest.promise)
latestRequest.resolve('latest-sort-result')
await latestTask
olderRequest.reject(new Error('stale request failed later'))
await olderTask
assertDeepEqual(
  requestState,
  { data: 'latest-sort-result', loadError: false, loading: false },
  '旧请求晚完成或失败时不得覆盖最新数据、错误和 loading 状态',
)

// —— 风险入口、多选分店与操作类型、收银员下钻 ——
{
  const base = {
    startUtc: '2026-10-01T00:00:00.000Z',
    endUtc: '2026-10-02T00:00:00.000Z',
    storeCode: '',
    cashierKeyword: '',
    deviceCode: '',
    deviceSystem: '',
    operationType: '',
    outcome: '',
    productKeyword: '',
    orderGuid: '',
    keyword: '',
    page: 1,
    pageSize: 20,
    sortBy: 'occurredAtUtc' as const,
    sortOrder: 'desc' as const,
  }
  const plain = buildOperationAuditQuery(base)
  assertEqual('riskLens' in plain || 'storeCodes' in plain || 'operationTypes' in plain || 'cashierId' in plain, false, '默认入口不带新参数，兼容旧后端')
  const abnormal = buildOperationAuditQuery({
    ...base,
    storeCodes: [' 1013', '1013', '', '1042'],
    operationTypes: ['CART_ITEM_REMOVE', 'CART_CLEAR'],
    cashierId: ' c1 ',
    riskLens: 'abnormal',
    ruleCodes: ['noSaleDrawer'],
    reviewStatus: 'pending',
  })
  assertEqual(JSON.stringify(abnormal.storeCodes), JSON.stringify(['1013', '1042']), '分店去空白去重')
  assertEqual(JSON.stringify(abnormal.operationTypes), JSON.stringify(['CART_ITEM_REMOVE', 'CART_CLEAR']), '操作类型多选')
  assertEqual(abnormal.cashierId, 'c1', '收银员编号去空白')
  assertEqual(abnormal.riskLens, 'abnormal', '异常入口')
  assertEqual(JSON.stringify(abnormal.ruleCodes), JSON.stringify(['noSaleDrawer']), '异常入口带规则')
  assertEqual(abnormal.reviewStatus, 'pending', '异常入口带核查状态')
  const danger = buildOperationAuditQuery({ ...base, riskLens: 'danger', ruleCodes: ['noSaleDrawer'], reviewStatus: 'pending' })
  assertEqual(danger.riskLens, 'danger', '危险入口')
  assertEqual('ruleCodes' in danger || 'reviewStatus' in danger, false, '规则与核查状态只在异常入口下传')
  const row = toLegacyEmployeeSummary({
    cashierId: 'c1', cashierName: 'Gao', storeCodes: ['1013'], deviceCodes: ['POS-1'], total: 3, dangerCount: 2,
    abnormalCount: 1, pendingReview: 1, abnormalByRule: [{ ruleCode: 'noSaleDrawer', count: 1 }], amountImpact: 19,
  })
  assertEqual(`${row.employeeId}/${row.employeeName}/${row.amountImpact}`, 'c1/Gao/19', '收银员汇总映射成员工汇总形状')
}

// 店长查看员工操作日志按全部关联分店（与后端 GetAssignedStoreScopeAsync 一致），新老收银两页都不能只列主分店。
{
  const legacyEmployeeLogsPageSource = readFileSync('src/pages/PosAdmin/LegacyEmployeeLogs/index.tsx', 'utf8')
  for (const [name, source] of [
    ['新收银', operationLogsPageSource],
    ['老收银', legacyEmployeeLogsPageSource],
  ] as const) {
    assertEqual(
      source.includes('buildStoreOptionsFromUserStores(currentUser?.stores)') &&
        !source.includes('manageableOnly: true') &&
        !source.includes('filterStoreOptionsByManagedCodes('),
      true,
      `${name}员工日志页店长分店选项应为全部关联分店，不只主分店`,
    )
  }
}

console.log('operationLogsLogic.test: ok')
