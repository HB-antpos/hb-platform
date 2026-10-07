import dayjs from 'dayjs'
import type { ContainerDetail } from '../../../types/container'
import type { ContainerDetailTableColumnKey, ContainerDetailTagStats } from './containerDetailLogic'
import {
  CONTAINER_DETAIL_VIEW_DEFAULT_COLUMN_WIDTHS,
  getContainerDetailViewDefaultColumnWidth,
  CONTAINER_DETAIL_NEW_STATE_TAGS,
  CONTAINER_DETAIL_PRODUCT_TYPE_TAGS,
  DEFAULT_CONTAINER_DETAIL_COLUMN_VIEW,
  applyContainerDetailSearchText,
  buildContainerDetailOverviewFacts,
  describeContainerDetailColumnFilters,
  getContainerDetailRowIssues,
  getContainerDetailTagGroupSelection,
  hasContainerDetailColumnFilterValues,
  isContainerDetailMatchPendingFilterActive,
  normalizeContainerDetailColumnView,
  removeContainerDetailColumnFilter,
  replaceContainerDetailTagGroup,
  resolveContainerDetailOverviewStats,
  resolveContainerDetailTagSegmentValue,
  resolveContainerDetailViewColumnKeys,
  summarizeContainerDetailManualDraft,
  switchContainerDetailSearchField,
  toggleContainerDetailMatchPendingFilter,
} from './containerDetailViewLogic'

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${label}。Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

// ---- 列视图 ----

assertEqual(normalizeContainerDetailColumnView('pricing'), 'pricing', '已知视图应原样保留')
assertEqual(normalizeContainerDetailColumnView('unknown'), DEFAULT_CONTAINER_DETAIL_COLUMN_VIEW, '未知视图应回退默认视图')
assertEqual(normalizeContainerDetailColumnView(null), 'cost', '本机没记过视图时默认成本核算')

const defaultOrder: ContainerDetailTableColumnKey[] = [
  'product', 'index', 'image', 'itemNumber', 'englishName', 'categoryName', 'containerPieces', 'packingQuantity',
  'containerQuantity', 'unitVolume', 'domesticPrice', 'transportCost', 'unitTransportCost', 'floatRate',
  'middlePackQuantity', 'warehouseImportPrice', 'importPrice', 'oemPrice', 'lastOEMPrice', 'newProduct',
  'productType', 'matchType', 'barcode', 'productName', 'warehouseStatus', 'remark', 'issues',
]
const fixedLeft = new Set<ContainerDetailTableColumnKey>(['product', 'index', 'image', 'itemNumber'])

const allViewKeys = resolveContainerDetailViewColumnKeys('all', defaultOrder, fixedLeft)
assertEqual(allViewKeys.includes('product') || allViewKeys.includes('issues'), false, '全部列不显示视图专用的合成列')
assertEqual(allViewKeys.length, defaultOrder.length - 2, '全部列保留原有全部业务列')
assertEqual(allViewKeys[0], 'index', '全部列沿用原列顺序')

assertDeepEqual(
  resolveContainerDetailViewColumnKeys('cost', defaultOrder, fixedLeft),
  ['product', 'containerPieces', 'packingQuantity', 'containerQuantity', 'unitVolume', 'domesticPrice', 'unitTransportCost', 'floatRate', 'importPrice', 'issues'],
  '成本核算视图按设计列出成本相关列',
)
assertDeepEqual(
  resolveContainerDetailViewColumnKeys('pricing', defaultOrder, fixedLeft),
  ['product', 'englishName', 'categoryName', 'warehouseImportPrice', 'importPrice', 'oemPrice', 'matchType', 'warehouseStatus'],
  '上架定价视图按当前列顺序列出定价相关列',
)
// 用户把合成商品列拖到了后面：左固定列仍要挪到最前，其余列保持拖拽后的相对顺序。
const draggedOrder: ContainerDetailTableColumnKey[] = ['containerPieces', 'floatRate', 'product', 'importPrice', 'issues']
assertDeepEqual(
  resolveContainerDetailViewColumnKeys('cost', draggedOrder, fixedLeft),
  ['product', 'containerPieces', 'floatRate', 'importPrice', 'issues'],
  '视图里左固定列必须排在最前，其余列沿用拖拽顺序',
)

// ---- 标签分组 ----

assertDeepEqual(
  replaceContainerDetailTagGroup(['set', 'new', 'noOemPrice'], CONTAINER_DETAIL_NEW_STATE_TAGS, ['existing']),
  ['set', 'noOemPrice', 'existing'],
  '替换新/已有分组时只换本组，其他组保持不变',
)
assertDeepEqual(
  replaceContainerDetailTagGroup(['set', 'multi', 'active'], CONTAINER_DETAIL_PRODUCT_TYPE_TAGS, []),
  ['active'],
  '清空类型分组不影响上下架分组',
)
assertDeepEqual(
  replaceContainerDetailTagGroup(['all', 'new'], CONTAINER_DETAIL_PRODUCT_TYPE_TAGS, ['multi', 'set']),
  ['new', 'set', 'multi'],
  '组内顺序按分组定义输出，并去掉无意义的 all',
)
assertDeepEqual(getContainerDetailTagGroupSelection(['new', 'set', 'noOemPrice'], CONTAINER_DETAIL_PRODUCT_TYPE_TAGS), ['set'], '读取本组已选标签')
assertEqual(resolveContainerDetailTagSegmentValue([], CONTAINER_DETAIL_NEW_STATE_TAGS), 'all', '本组没选时分段显示全部')
assertEqual(resolveContainerDetailTagSegmentValue(['set', 'new'], CONTAINER_DETAIL_NEW_STATE_TAGS), 'new', '本组只选一个时分段高亮该项')
assertEqual(resolveContainerDetailTagSegmentValue(['new', 'existing'], CONTAINER_DETAIL_NEW_STATE_TAGS), 'all', '本组全选等于不过滤，分段显示全部')

// ---- 匹配待确认 ----

assertEqual(isContainerDetailMatchPendingFilterActive({}), false, '没有匹配方式筛选时未激活')
assertDeepEqual(toggleContainerDetailMatchPendingFilter({ itemNumber: 'HB' }), { itemNumber: 'HB', matchTypes: ['supplierItem'] }, '点选匹配待确认只看候选需确认')
assertDeepEqual(
  toggleContainerDetailMatchPendingFilter({ matchTypes: ['productCode'] }),
  { matchTypes: ['supplierItem'] },
  '点选时替换列头里已有的其他匹配方式，列表才会明显收窄',
)
assertDeepEqual(toggleContainerDetailMatchPendingFilter({ matchTypes: ['supplierItem'] }), {}, '再次点选取消时删掉空的匹配方式筛选')
assertDeepEqual(
  toggleContainerDetailMatchPendingFilter({ matchTypes: ['supplierItem', 'unmatched'] }),
  { matchTypes: ['unmatched'] },
  '取消时只去掉候选需确认，保留列头里的其他选择',
)

// ---- 行问题标签 ----

assertDeepEqual(
  getContainerDetailRowIssues({ id: 1, hguid: 'a', 是否新商品: true, 贴牌价格: 0, 进口价格: 0, matchType: 'supplierItem' } as ContainerDetail),
  ['missingRetailPrice', 'missingImportPrice', 'matchPending'],
  '新商品缺零售价、进口价为 0、候选匹配应同时标出三个问题',
)
assertDeepEqual(
  getContainerDetailRowIssues({ id: 2, hguid: 'b', 是否新商品: false, 贴牌价格: undefined, 进口价格: 3.2, matchType: 'productCode' } as ContainerDetail),
  [],
  '已有商品缺零售价不算问题（口径与「缺零售价」标签一致），进口价有值也不算',
)

// ---- 概况：统计口径 ----

const stats = (all: number) => ({ all, new: 1, existing: all - 1 }) as unknown as ContainerDetailTagStats
const localStats = stats(12)
const remoteStats = stats(500)
const cachedStats = stats(480)
assertEqual(hasContainerDetailColumnFilterValues({ itemNumber: '  ' }), false, '空白文字筛选不算生效')
assertEqual(hasContainerDetailColumnFilterValues({ importPrice: { min: 0 } }), true, '数字下限 0 也算生效')
assertEqual(hasContainerDetailColumnFilterValues({ productTypes: [] }), false, '空枚举不算生效')
assertDeepEqual(
  resolveContainerDetailOverviewStats({ loadMode: 'full', allRowsStats: localStats, remoteStats: null, remoteStatsIsCurrent: false, hasColumnFilters: true, cachedStats: null }),
  { stats: localStats, cacheable: true },
  '全量模式直接按全部行本地统计，不受列头筛选影响',
)
assertDeepEqual(
  resolveContainerDetailOverviewStats({ loadMode: 'paged', allRowsStats: localStats, remoteStats, remoteStatsIsCurrent: true, hasColumnFilters: false, cachedStats: null }),
  { stats: remoteStats, cacheable: true },
  '分页模式无列头筛选且统计已对应当前查询时采用服务端整柜统计',
)
assertDeepEqual(
  resolveContainerDetailOverviewStats({ loadMode: 'paged', allRowsStats: localStats, remoteStats, remoteStatsIsCurrent: true, hasColumnFilters: true, cachedStats }),
  { stats: cachedStats, cacheable: false },
  '分页模式有列头筛选时服务端统计只是筛选内口径，应沿用缓存的整柜统计',
)
assertDeepEqual(
  resolveContainerDetailOverviewStats({ loadMode: 'paged', allRowsStats: localStats, remoteStats, remoteStatsIsCurrent: false, hasColumnFilters: false, cachedStats: null }),
  { stats: null, cacheable: false },
  '统计尚未对应当前查询且没有缓存时显示 --',
)
assertDeepEqual(
  resolveContainerDetailOverviewStats({ loadMode: 'probe', allRowsStats: localStats, remoteStats: null, remoteStatsIsCurrent: false, hasColumnFilters: false, cachedStats }),
  { stats: cachedStats, cacheable: false },
  '首屏探测阶段沿用已有缓存',
)

// ---- 概况：事实格 ----

const today = dayjs('2026-10-06')
const facts = buildContainerDetailOverviewFacts(
  {
    状态: 1,
    装柜日期: '2026-09-08T00:00:00',
    预计到岸日期: '2026-10-06T00:00:00',
    实际到货日期: undefined,
    运费: 3420,
    总体积: 66.8,
  },
  today,
)
assertEqual(facts.loadingDate, '2026-09-08', '装柜日期按自然日显示')
assertEqual(facts.loadingWeek, 37, '装柜日期给出 ISO 周号')
assertEqual(facts.etaDate, '2026-10-06', '预计到岸按自然日显示')
assertDeepEqual(facts.etaHint, { kind: 'today' }, '运输中且未到货、预计今天到岸时提示今天')
assertEqual(facts.actualArrivalDate, undefined, '未到货时没有实际到货日期')
assertEqual(facts.standard68Freight, 3481.44, '运费按 68 m³ 标准柜折算并保留 2 位')
assertEqual(facts.loadRatePercent, 98, '装载率按 68 m³ 计算')
const overdueFacts = buildContainerDetailOverviewFacts(
  { 状态: 0, 装柜日期: undefined, 预计到岸日期: '2026-10-01', 实际到货日期: undefined, 运费: 100, 总体积: 0 },
  today,
)
assertDeepEqual(overdueFacts.etaHint, { kind: 'overdue', days: 5 }, '已装柜未到货且过了预计日期时提示逾期天数')
assertEqual(overdueFacts.standard68Freight, undefined, '总体积无效时不折算标准运费')
assertDeepEqual(
  buildContainerDetailOverviewFacts(
    { 状态: 2, 装柜日期: undefined, 预计到岸日期: '2026-10-01', 实际到货日期: '2026-10-03', 运费: undefined, 总体积: undefined },
    today,
  ).etaHint,
  { kind: 'week', week: 40 },
  '已完成货柜只显示周号，不标逾期',
)
assertDeepEqual(buildContainerDetailOverviewFacts(null, today), { etaHint: { kind: 'none' } }, '货柜未加载时没有事实值')

// ---- 保存状态条：手动草稿计数 ----

const manualSummary = summarizeContainerDetailManualDraft(
  {
    'row-1': { hguid: 'row-1', 进口价格: 3.5, 单件装箱数: 24 },
    'row-2': { hguid: 'row-2', 贴牌价格: 9.99, ClearEnglishName: true },
    'row-3': { hguid: 'row-3', 备注: '旧草稿', ProductCategoryGUID: 'cat-1' },
  },
  {
    // row-1 的单件装箱数还在自动保存队列里，由自动保存状态展示。
    'row-1': { 单件装箱数: 24 },
  },
)
assertDeepEqual(
  manualSummary,
  { total: 5, importPrice: 1, retailPrice: 1, englishName: 1, other: 2 },
  '队列里的自动保存字段不计入待保存；清除英文名称算英文名称；不在队列里的旧自动保存字段与分类算其他',
)
assertDeepEqual(
  summarizeContainerDetailManualDraft({}, {}),
  { total: 0, importPrice: 0, retailPrice: 0, englishName: 0, other: 0 },
  '没有草稿时全为 0',
)

// ---- 已生效筛选条 ----

assertDeepEqual(
  describeContainerDetailColumnFilters({
    remark: '加急',
    itemNumber: ' HB01 ',
    importPrice: { min: 1 },
    floatRate: {},
    productTypes: ['set', 'multi'],
    barcode: '   ',
    matchTypes: [],
  }),
  [
    { key: 'itemNumber', kind: 'text', value: 'HB01' },
    { key: 'importPrice', kind: 'range', min: 1 },
    { key: 'productTypes', kind: 'enum', values: ['set', 'multi'] },
    { key: 'remark', kind: 'text', value: '加急' },
  ],
  '列头筛选按列顺序整理，空白文字、空区间和空枚举不显示',
)
assertDeepEqual(removeContainerDetailColumnFilter({ itemNumber: 'A', barcode: 'B' }, 'itemNumber'), { barcode: 'B' }, '移除单个列头筛选')

// ---- 工具栏搜索 ----

const baseFilters = { barcode: '952' }
assertEqual(applyContainerDetailSearchText(baseFilters, 'barcode', '952'), baseFilters, '值未变时返回原对象，避免触发重载')
assertDeepEqual(applyContainerDetailSearchText(baseFilters, 'itemNumber', 'HB'), { barcode: '952', itemNumber: 'HB' }, '写入对应列头文字筛选')
assertDeepEqual(applyContainerDetailSearchText({ itemNumber: 'HB' }, 'itemNumber', '  '), {}, '清空搜索时删掉该列筛选')
assertEqual(applyContainerDetailSearchText(baseFilters, 'itemNumber', ''), baseFilters, '本来就没有条件时清空不产生新对象')
assertDeepEqual(
  switchContainerDetailSearchField({ itemNumber: 'LED' }, 'itemNumber', 'productName'),
  { filters: { productName: 'LED' }, text: 'LED' },
  '切换字段时目标字段为空则把关键字挪过去',
)
assertDeepEqual(
  switchContainerDetailSearchField({ itemNumber: 'LED', productName: '灯' }, 'itemNumber', 'productName'),
  { filters: { itemNumber: 'LED', productName: '灯' }, text: '灯' },
  '目标字段已有条件时两边都保持原样，搜索框显示目标字段的值',
)
assertDeepEqual(
  switchContainerDetailSearchField({}, 'itemNumber', 'barcode'),
  { filters: {}, text: '' },
  '两边都空时只切换字段',
)

// 两个精简视图的默认列宽（加 56px 勾选列）都要放得进 1440 宽屏的表格可用宽度，最右的「问题」列不能被挤出视野。
for (const view of ['cost', 'pricing'] as const) {
  const widths = CONTAINER_DETAIL_VIEW_DEFAULT_COLUMN_WIDTHS[view]
  const total = 56 + Object.values(widths).reduce((sum, width) => sum + (width ?? 0), 0)
  if (total > 1140) {
    throw new Error(`${view} 视图默认列宽合计 ${total}px 超出 1440 宽屏可用宽度`)
  }
}
if (getContainerDetailViewDefaultColumnWidth('all', 'product') !== undefined) {
  throw new Error('「全部列」不应套用精简视图的默认列宽')
}
if (getContainerDetailViewDefaultColumnWidth('cost', 'importPrice') !== CONTAINER_DETAIL_VIEW_DEFAULT_COLUMN_WIDTHS.cost.importPrice) {
  throw new Error('成本核算视图应返回自己的默认列宽')
}

console.log('containerDetailViewLogic.test: ok')
