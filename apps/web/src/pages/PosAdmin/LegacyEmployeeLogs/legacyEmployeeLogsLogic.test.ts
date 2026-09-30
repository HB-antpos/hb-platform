import dayjs from 'dayjs'

import {
  HIGH_RISK_OPERATIONS,
  KNOWN_LEGACY_OPERATIONS,
  QUICK_FILTER_OPERATIONS,
  buildLegacyLogQuery,
  buildOperationOptions,
  createLatestRequestGuard,
  getDayRange,
  getOperationCategory,
  getUploadLagMinutes,
  isHighRiskOperation,
  isQuickFilterActive,
  normalizeStoreCodes,
  parseLegacyDetail,
  resolveInitialStores,
  splitLagMinutes,
  storeSelectionKey,
  sumOperationCounts,
  validateTimeRange,
} from './legacyEmployeeLogsLogic'

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

// —— 详情文本解析（样例取自生产抽样的真实格式）——
assertDeepEqual(
  parseLegacyDetail('从购物车删除商品: XMAS ＄2 CARDS，编码:xmascard2，数量:5，单价:2.00，总金额:10.00', '删除商品'),
  {
    texts: [],
    fields: [
      { key: '编码', value: 'xmascard2' },
      { key: '数量', value: '5' },
      { key: '单价', value: '2.00' },
      { key: '总金额', value: '10.00', tone: 'danger' },
    ],
    productName: 'XMAS ＄2 CARDS',
  },
  '删除商品：商品名单独提取，删除金额标红',
)

const payment = parseLegacyDetail('订单:9F2D5CDB-53F3-4856-A0BC-16991AD3B6A8支付成功，金额:3.99', '支付完成')
assertEqual(payment.orderGuid, '9F2D5CDB-53F3-4856-A0BC-16991AD3B6A8', '支付完成：提取订单号')
assertDeepEqual(payment.texts, ['支付成功'], '支付完成：订单号之后的说明保留')
assertDeepEqual(payment.fields, [{ key: '金额', value: '3.99', tone: 'money' }], '支付金额标为收款')

const reprint = parseLegacyDetail('重打印订单:0032879C-3D40-403C-9B58-CEF9D97AECF5，金额:15.46', '重打印')
assertEqual(reprint.orderGuid, '0032879C-3D40-403C-9B58-CEF9D97AECF5', '重打印：订单号从「重打印订单」键提取')

assertDeepEqual(
  parseLegacyDetail('开始结账，共3件商品，总金额:14.00', '结账'),
  { texts: ['开始结账', '共3件商品'], fields: [{ key: '总金额', value: '14.00' }] },
  '无键片段原样保留',
)

assertDeepEqual(
  parseLegacyDetail('将所有商品折扣率设置为:50%，购物车商品数:9，原总金额:49.68', '修改所有商品折扣').fields,
  [
    { key: '折扣率', value: '50%', tone: 'danger' },
    { key: '购物车商品数', value: '9' },
    { key: '原总金额', value: '49.68' },
  ],
  '整单折扣：长键改为短名并标红',
)

assertEqual(
  parseLegacyDetail('商品:2dollar cards\n，编码:A500-1020，单价:2.00', '添加商品').productName,
  '2dollar cards',
  '商品名里的换行被裁掉',
)
assertEqual(
  parseLegacyDetail('商品:Mug, blue，编码:M1', '添加商品').productName,
  'Mug, blue',
  '只按全角逗号切分，商品名里的半角逗号保留',
)
assertDeepEqual(
  parseLegacyDetail('员工 Abigal 确认身份进行开钱箱操作', '身份确认'),
  { texts: ['员工 Abigal 确认身份进行开钱箱操作'], fields: [] },
  '无结构文本原样显示',
)
assertDeepEqual(parseLegacyDetail(null, null), { texts: [], fields: [] }, '空详情')
assertEqual(
  parseLegacyDetail('商品:Festive kids socks，编码:9333527762689，原数量:1，新数量:-1，单价:2.50', '无小票退货成功')
    .fields.find((field) => field.key === '新数量')?.tone,
  'danger',
  '无小票退货的新数量标红',
)

// —— 操作分类与快捷筛选 ——
assertEqual(getOperationCategory('删除商品'), 'delete', '删除归类')
assertEqual(getOperationCategory(' 开钱箱 '), 'auth', '名称前后空白不影响归类')
assertEqual(getOperationCategory('未来新增的操作'), 'other', '未知操作归入其他')
assertEqual(isHighRiskOperation('修改所有商品折扣'), true, '整单折扣是高风险')
assertEqual(isHighRiskOperation('添加商品'), false, '加购不是高风险')
assertEqual(new Set(KNOWN_LEGACY_OPERATIONS).size, KNOWN_LEGACY_OPERATIONS.length, '已知操作名称不得重复')
HIGH_RISK_OPERATIONS.forEach((operation) => {
  assertEqual(KNOWN_LEGACY_OPERATIONS.includes(operation), true, `高风险操作 ${operation} 必须是已知操作`)
})
assertEqual(isQuickFilterActive('price', ['修改所有商品折扣', '修改商品价格', '修改商品折扣']), true, '快捷筛选顺序无关')
assertEqual(isQuickFilterActive('price', ['修改商品价格']), false, '部分选择不算命中')
assertEqual(isQuickFilterActive('highRisk', undefined), false, '未选择不命中')
assertEqual(QUICK_FILTER_OPERATIONS.return.includes('无小票退货成功'), true, '退货快捷筛选包含无小票退货')

const counts = [
  { operation: '添加商品', count: 30 },
  { operation: '删除商品', count: 4 },
  { operation: '新版未知操作', count: 2 },
  { operation: null, count: 1 },
]
const options = buildOperationOptions(counts)
assertEqual(options[0].operation, KNOWN_LEGACY_OPERATIONS[0], '下拉先列已知操作')
assertDeepEqual(options[options.length - 1], { operation: '新版未知操作', count: 2, category: 'other' }, '未知操作补在最后并带计数')
assertEqual(options.find((row) => row.operation === '删除商品')?.count, 4, '已知操作带上计数')
assertEqual(sumOperationCounts(counts, ['删除商品', '开钱箱']), 4, '按操作累加计数')

// —— 上传滞后 ——
assertEqual(getUploadLagMinutes('2026-09-30T16:12:48', '2026-09-30T16:17:02'), 4, '滞后按整分钟向下取')
assertEqual(getUploadLagMinutes('2026-09-30T16:12:48', '2026-09-30T16:10:00'), null, '上传早于操作（时钟偏差）不显示')
assertEqual(getUploadLagMinutes('bad', '2026-09-30T16:10:00'), null, '无法解析不显示')
assertDeepEqual(splitLagMinutes(343), { days: 0, hours: 5, minutes: 43 }, '拆分小时与分钟')
assertDeepEqual(splitLagMinutes(1500), { days: 1, hours: 1, minutes: 0 }, '拆分天数')

// —— 时间范围与查询参数 ——
const day = getDayRange(dayjs('2026-09-30T10:00:00'))
assertEqual(validateTimeRange(day), null, '单日范围有效')
assertEqual(validateTimeRange(undefined), 'required', '范围必填')
assertEqual(
  validateTimeRange([dayjs('2026-09-01T00:00:00'), dayjs('2026-10-02T00:00:00')]),
  'tooLong',
  '超过 31 天拒绝',
)
assertEqual(
  validateTimeRange([dayjs('2026-09-01T00:00:00'), dayjs('2026-10-01T23:59:00')]),
  null,
  '恰好 31 天（9-01 00:00 ~ 10-01 23:59）允许',
)
assertEqual(
  validateTimeRange([dayjs('2026-09-30T10:00:00'), dayjs('2026-09-30T09:00:00')]),
  'reversed',
  '结束早于开始拒绝',
)

assertDeepEqual(
  buildLegacyLogQuery(
    {
      timeRange: day,
      storeCodes: [' 1013 ', '1022', '1013'],
      deviceCode: ' ',
      employeeIds: [],
      operations: ['删除商品'],
      keyword: ' xmascard2 ',
    },
    { pageNumber: 2, pageSize: 50, sortOrder: 'desc' },
  ),
  {
    storeCodes: ['1013', '1022'],
    from: '2026-09-30T00:00:00',
    to: '2026-10-01T00:00:00',
    operations: ['删除商品'],
    keyword: 'xmascard2',
    pageNumber: 2,
    pageSize: 50,
    sortOrder: 'desc',
  },
  '单日选择按墙钟传半开区间，空条件不传',
)
assertEqual(buildLegacyLogQuery({ timeRange: day }, { pageNumber: 1, pageSize: 50, sortOrder: 'desc' }), null, '没选分店不发请求')

assertEqual(
  buildLegacyLogQuery({ timeRange: day, storeCodes: [' ', ''] }, { pageNumber: 1, pageSize: 50, sortOrder: 'desc' }),
  null,
  '分店全是空白时不发请求',
)

// —— 分店多选 ——
assertDeepEqual(normalizeStoreCodes([' 1022', '1013', '1022', null, '']), ['1022', '1013'], '分店去空白去重并保持顺序')
assertEqual(storeSelectionKey(['1022', '1013']), storeSelectionKey(['1013', ' 1022 ']), '选择比较与顺序无关')
assertEqual(storeSelectionKey([]), '', '空选择的比较键为空')
assertDeepEqual(resolveInitialStores('["1013","1099","1022"]', ['1013', '1022', '1004']), ['1013', '1022'], '只恢复仍可选的分店')
assertDeepEqual(resolveInitialStores('1013', ['1013', '1022']), ['1013'], '兼容早期单个编码的纯文本')
assertDeepEqual(resolveInitialStores('["1099"]', ['1004']), ['1004'], '记住的都不可选且只有一个可选分店时直接选中')
assertDeepEqual(resolveInitialStores(null, ['1004', '1013']), [], '没有记录且有多个可选分店时不预选')
assertDeepEqual(resolveInitialStores('{bad json', ['1004', '1013']), [], '无法解析时按原文当编码，不可选即丢弃')

const guard = createLatestRequestGuard()
const first = guard.begin()
const second = guard.begin()
assertEqual(guard.isLatest(first), false, '旧请求结果丢弃')
assertEqual(guard.isLatest(second), true, '最新请求结果采用')

console.log('legacy employee logs logic tests passed')
