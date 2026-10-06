import {
  DEFAULT_SUPPLIER_SORT,
  SUPPLIER_COLUMN_WIDTHS,
  SUPPLIER_NAME_COLUMN,
  SUPPLIER_SORT_FIELD_BY_COLUMN,
  SUPPLIER_TABLE_MIN_WIDTH,
  buildFailureClipboardText,
  buildSavePayload,
  formatCreatedDate,
  normalizeKeyword,
  normalizePhotoUrl,
  normalizeSyncResult,
  parseSyncFailure,
  resolveOverflowPage,
  resolveSupplierSort,
  resolveSupplierTableLayout,
  sortOrderForColumn,
  statusFilterToParam,
  statusParamToFilter,
  toFormValues,
} from './chinaSuppliersLogic'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`)
  }
}

function assertJson(actual: unknown, expected: unknown, message: string) {
  const left = JSON.stringify(actual)
  const right = JSON.stringify(expected)
  if (left !== right) {
    throw new Error(`${message}: expected ${right}, got ${left}`)
  }
}

// ---- 排序键映射：只发后端认识的键 ----
const backendKeys = new Set(['suppliercode', 'suppliername', 'shopnumber', 'contactperson', 'phone', 'status', 'createdate'])
for (const [column, field] of Object.entries(SUPPLIER_SORT_FIELD_BY_COLUMN)) {
  assert(backendKeys.has(field), `列 ${column} 映射到的排序键 ${field} 后端不认识`)
}
assertJson(resolveSupplierSort('supplierName', 'ascend'), { sortField: 'suppliername', sortDirection: 'asc' }, '供应商列升序')
assertJson(resolveSupplierSort('shopNumber', 'descend'), { sortField: 'shopnumber', sortDirection: 'desc' }, '店铺号列降序')
assertJson(resolveSupplierSort('contactPerson', 'ascend'), { sortField: 'contactperson', sortDirection: 'asc' }, '联系人列升序')
assertJson(resolveSupplierSort('status', 'ascend'), { sortField: 'status', sortDirection: 'asc' }, '状态列升序')
assertJson(resolveSupplierSort('createdAt', 'ascend'), { sortField: 'createdate', sortDirection: 'asc' }, '创建时间列升序')
// 旧页面发过的 createdAt / updatedAt 以及取消排序都回到默认排序，不再把假键发给后端。
assertJson(resolveSupplierSort('updatedAt', 'ascend'), DEFAULT_SUPPLIER_SORT, '未知列回到默认排序')
assertJson(resolveSupplierSort('supplierName', undefined), DEFAULT_SUPPLIER_SORT, '取消排序回到默认排序')
assertJson(resolveSupplierSort('supplierName', null), DEFAULT_SUPPLIER_SORT, 'order 为 null 回到默认排序')
assertJson(resolveSupplierSort(undefined, 'ascend'), DEFAULT_SUPPLIER_SORT, '没有 columnKey 回到默认排序')
assertJson(DEFAULT_SUPPLIER_SORT, { sortField: 'createdate', sortDirection: 'desc' }, '默认排序为创建时间倒序')
// 返回的是副本，调用方改动不能污染默认值。
const mutated = resolveSupplierSort(undefined, null)
mutated.sortDirection = 'asc'
assertEqual(DEFAULT_SUPPLIER_SORT.sortDirection, 'desc', '默认排序不得被改写')

assertEqual(sortOrderForColumn('createdAt', DEFAULT_SUPPLIER_SORT), 'descend', '默认状态下创建时间列显示降序箭头')
assertEqual(sortOrderForColumn('supplierName', DEFAULT_SUPPLIER_SORT), null, '默认状态下其它列没有箭头')
assertEqual(sortOrderForColumn('supplierName', { sortField: 'suppliername', sortDirection: 'asc' }), 'ascend', '供应商列升序箭头')
assertEqual(sortOrderForColumn('createdAt', { sortField: 'suppliername', sortDirection: 'asc' }), null, '排序切到别的列后创建时间列箭头消失')

// ---- 状态筛选：禁用 = 0 是有效值 ----
assertEqual(statusFilterToParam('all'), undefined, '全部不传 status')
assertEqual(statusFilterToParam('enabled'), 1, '启用 = 1')
assertEqual(statusFilterToParam('disabled'), 0, '禁用 = 0（不能被当成空）')
assertEqual(statusParamToFilter(undefined), 'all', '无 status = 全部')
assertEqual(statusParamToFilter(1), 'enabled', '1 = 启用')
assertEqual(statusParamToFilter(0), 'disabled', '0 = 禁用')

// 对象合并：显式 undefined 必须能「清除筛选」（这正是不用默认参数的原因）。
const baseQuery = { page: 3, pageSize: 20, search: '义乌', status: 1 as number | undefined }
const cleared = { ...baseQuery, ...{ page: 1, status: undefined } }
assertEqual(cleared.status, undefined, '显式 undefined 覆盖应清除状态筛选')
assertEqual(cleared.search, '义乌', '未覆盖的字段保持不变')
assertEqual(cleared.page, 1, '覆盖页码')

assertEqual(normalizeKeyword('  HB012 '), 'HB012', '关键词去首尾空白')
assertEqual(normalizeKeyword('   '), undefined, '纯空白不发送')
assertEqual(normalizeKeyword(''), undefined, '空串不发送')
assertEqual(normalizeKeyword(undefined), undefined, 'undefined 不发送')

// ---- 翻页兜底 ----
assertEqual(resolveOverflowPage({ page: 3, pageSize: 20, total: 41, itemCount: 0 }), null, '页码仍在有效范围内（共 3 页）却为空，不回退，避免来回请求')
assertEqual(resolveOverflowPage({ page: 4, pageSize: 20, total: 41, itemCount: 0 }), 3, '删除后第 4 页越界应回退到最后一页')
assertEqual(resolveOverflowPage({ page: 3, pageSize: 20, total: 40, itemCount: 0 }), 2, '总数刚好 40 时最后一页是第 2 页')
assertEqual(resolveOverflowPage({ page: 2, pageSize: 20, total: 0, itemCount: 0 }), null, '总数为 0 时不回退（是真的没有数据）')
assertEqual(resolveOverflowPage({ page: 1, pageSize: 20, total: 0, itemCount: 0 }), null, '第一页为空不回退')
assertEqual(resolveOverflowPage({ page: 2, pageSize: 20, total: 25, itemCount: 5 }), null, '本页有数据不回退')

// ---- 展示 ----
assertJson(formatCreatedDate('2026-08-21 10:30:05'), { text: '2026-08-21', full: '2026-08-21 10:30:05' }, '创建时间只截日期，完整时间放 tooltip')
assertJson(formatCreatedDate('2026-08-21T10:30:05'), { text: '2026-08-21', full: '2026-08-21 10:30:05' }, 'ISO 文本同样按文本截取，不做时区换算')
assertJson(formatCreatedDate('未知'), { text: '未知', full: '未知' }, '解析不出日期时原样显示')
assertEqual(formatCreatedDate(''), null, '空串无展示')
assertEqual(formatCreatedDate(undefined), null, 'undefined 无展示')

assertEqual(normalizePhotoUrl(' https://cdn.example.com/a.jpg '), 'https://cdn.example.com/a.jpg', 'https 链接可预览（去空白）')
assertEqual(normalizePhotoUrl('http://cdn.example.com/a.jpg'), 'http://cdn.example.com/a.jpg', 'http 链接可预览')
assertEqual(normalizePhotoUrl('javascript:alert(1)'), undefined, 'javascript: 不可预览')
assertEqual(normalizePhotoUrl('data:image/png;base64,AAAA'), undefined, 'data: 不可预览')
assertEqual(normalizePhotoUrl('//cdn.example.com/a.jpg'), undefined, '无协议的链接不可预览')
assertEqual(normalizePhotoUrl('https://a b.com'), undefined, '含空白的链接不可预览')
assertEqual(normalizePhotoUrl(''), undefined, '空串不可预览')

// ---- 保存载荷：空串邮箱必须变成缺省字段，否则后端 [EmailAddress] 返回 400 ----
const payload = buildSavePayload({
  supplierCode: ' HB012 ',
  supplierName: ' 义乌市嘉悦日用品商行 ',
  shopNumber: '  ',
  contactPerson: '陈嘉悦',
  phone: '',
  email: '',
  storefrontPhoto: undefined,
  status: 1,
  remarks: '\n ',
})
assertEqual(payload.supplierCode, 'HB012', '编码去首尾空白')
assertEqual(payload.supplierName, '义乌市嘉悦日用品商行', '名称去首尾空白')
assertEqual(payload.email, undefined, '空串邮箱不发送')
assertEqual(payload.shopNumber, undefined, '纯空白店铺号不发送')
assertEqual(payload.phone, undefined, '空串电话不发送')
assertEqual(payload.remarks, undefined, '纯空白备注不发送')
assertEqual(payload.contactPerson, '陈嘉悦', '有值的可选字段原样保留')
assert(!('email' in JSON.parse(JSON.stringify(payload))), '序列化为 JSON 后不应出现 email 键')
assertEqual(buildSavePayload({ supplierCode: 'A', supplierName: 'B', status: 0 }).status, 0, '状态 0（禁用）必须保留')
assertEqual(buildSavePayload({ supplierCode: 'A', supplierName: 'B' }).status, 1, '未选状态默认启用')
assertEqual(buildSavePayload({ supplierCode: 'A', supplierName: 'B', email: ' a@b.com ' }).email, 'a@b.com', '邮箱去空白')

const formValues = toFormValues({
  guid: 'g1',
  supplierCode: 'HB012',
  supplierName: 'N',
  status: 0,
  email: 'a@b.com',
})
assertEqual(formValues.status, 0, '编辑表单保留禁用状态')
assertEqual(formValues.email, 'a@b.com', '编辑表单带出邮箱')
assertEqual(formValues.supplierCode, 'HB012', '编辑表单带出编码')

// ---- 同步结果 ----
const success = normalizeSyncResult({ totalProcessed: 3, successCount: 3, failCount: 0, insertedCount: 2, updatedCount: 1, errors: [] })
assertEqual(success.outcome, 'success', '全部成功')
assertEqual(success.unmatchedCount, 0, '全部成功时没有未找到的条目')

const partial = normalizeSyncResult({
  totalProcessed: 3,
  successCount: 2,
  failCount: 1,
  insertedCount: 1,
  updatedCount: 1,
  errors: ['HB024(浙江盛达五金工具): 销售库中已存在同店铺号的不同供应商'],
})
assertEqual(partial.outcome, 'partial', '有失败且有成功 = 部分失败（不得是 success）')
assertEqual(partial.failCount, 1, '失败数')
assertEqual(partial.errors.length, 1, '失败明细原样带回')

const allFailed = normalizeSyncResult({ totalProcessed: 2, successCount: 0, failCount: 2, insertedCount: 0, updatedCount: 0, errors: ['A(a): x', 'B(b): y'] })
assertEqual(allFailed.outcome, 'failed', '全部失败')

// 接口漏报 failCount 但给了明细：以明细条数兜底，仍不是 success。
const missingCount = normalizeSyncResult({ totalProcessed: 2, insertedCount: 1, updatedCount: 0, errors: ['B(b): y'] })
assertEqual(missingCount.failCount, 1, '失败数以明细条数兜底')
assertEqual(missingCount.outcome, 'partial', '有明细即有失败')

// 勾选的条目在库里已找不到：既不是成功也不是失败，但也不能算全部成功。
const unmatched = normalizeSyncResult({ totalProcessed: 3, successCount: 2, failCount: 0, insertedCount: 1, updatedCount: 1, errors: [] })
assertEqual(unmatched.unmatchedCount, 1, '共处理 3、成功 2、失败 0 → 1 条未找到')
assertEqual(unmatched.outcome, 'partial', '存在未找到的条目不得显示成功')

// 脏数据防御：null / 字符串数字 / 非字符串明细。
const dirty = normalizeSyncResult({ totalProcessed: '2' as unknown as number, insertedCount: 2, errors: [null, '  ', 3] as unknown as string[] })
assertEqual(dirty.outcome, 'success', '非法明细被过滤后按数字判定')
assertEqual(dirty.errors.length, 0, '非字符串 / 空白明细被过滤')
assertEqual(normalizeSyncResult(null).outcome, 'success', 'null 返回值按空结果处理')
assertEqual(normalizeSyncResult(undefined).totalProcessed, 0, 'undefined 返回值总数为 0')

// 失败明细解析：粗体标识 + 原因；解析不出来原样显示。
assertJson(
  parseSyncFailure('HB024(浙江盛达五金工具): 销售库中已存在同店铺号的不同供应商'),
  { label: 'HB024 浙江盛达五金工具', reason: '销售库中已存在同店铺号的不同供应商' },
  '标准格式拆成标识和原因',
)
assertJson(
  parseSyncFailure('HB031(义乌(利发)文具): Timeout expired'),
  { label: 'HB031 义乌(利发)文具', reason: 'Timeout expired' },
  '名称里含括号时按第一处「): 」分隔',
)
assertJson(parseSyncFailure('HB009(): 原因'), { label: 'HB009', reason: '原因' }, '名称为空时只显示编码')
assertJson(parseSyncFailure('连接 HBSales 超时'), { label: '', reason: '连接 HBSales 超时' }, '非标准格式整行当原因')
assertEqual(buildFailureClipboardText({ errors: ['a', 'b'] }), 'a\nb', '复制文本一行一条，用后端原文')
assertEqual(buildFailureClipboardText({ errors: [] }), '', '没有明细时为空串')

// 列宽：供应商列有上下限，不再吃掉全部剩余宽度（大屏上曾被拉到上千像素）。
const supplierFixed = Object.values(SUPPLIER_COLUMN_WIDTHS).reduce((sum, width) => sum + width, 0)
assertEqual(SUPPLIER_TABLE_MIN_WIDTH, supplierFixed + SUPPLIER_NAME_COLUMN.min, '最窄表格宽度 = 固定列合计 + 供应商列最窄宽度')
// 1280 视口表格区约 952px（卡片内边距 24×2）：最窄宽度不能超过它，否则出现横向滚动。
assert(SUPPLIER_TABLE_MIN_WIDTH <= 952, `供应商表格最窄宽度 ${SUPPLIER_TABLE_MIN_WIDTH} 超过 1280 视口表格区 952px`)
assert(SUPPLIER_COLUMN_WIDTHS.serial >= 53, '序号列至少 53px 才能放下 5 位数序号（等宽 12px 数字约 33px + padding 20px）')
assert(SUPPLIER_COLUMN_WIDTHS.action >= 86, '操作列至少 86px 才能容纳「编辑 ⋯」按钮组')
const supplierNarrow = resolveSupplierTableLayout(952)
assert(supplierNarrow.nameWidth >= SUPPLIER_NAME_COLUMN.min, '1280 视口供应商列不小于最窄宽度')
assertEqual(supplierNarrow.tableWidth, supplierFixed + supplierNarrow.nameWidth, 'scroll.x = 固定列合计 + 供应商列宽')
assert(supplierNarrow.tableWidth <= 952, '1280 视口 scroll.x 不超过表格区宽度')
const supplierTooNarrow = resolveSupplierTableLayout(700)
assertEqual(supplierTooNarrow.nameWidth, SUPPLIER_NAME_COLUMN.min, '容器比最窄宽度还窄时停在最窄值（由 scroll.x 触发横向滚动，而不是把列压扁）')
const supplierWide = resolveSupplierTableLayout(2200)
assertEqual(supplierWide.nameWidth, SUPPLIER_NAME_COLUMN.max, '超宽屏供应商列封顶')
assert(supplierWide.tableWidth < 2200, '超宽屏下表格最小宽度小于容器，多出来的空间由没设宽度的操作列吸收')

console.log('ChinaSuppliers chinaSuppliersLogic.test.ts: ok')
