import assert from 'node:assert/strict'
import { renderToStaticMarkup } from 'react-dom/server'
import type { LocalSupplierCategoryNode, LocalSupplierCategorySummary } from '../../../types/localSupplierCategory'
import SupplierCategoryCell from './SupplierCategoryCell'
import SupplierCategoryFormField from './SupplierCategoryFormField'
import SupplierCategoryManagerPanel, { filterWebsiteSupplierSummaries } from './SupplierCategoryManagerPanel'
import SupplierCategoryTreeNodeTitle from './SupplierCategoryTreeNodeTitle'

// 测试环境未初始化 i18n：react-i18next 直接返回默认中文文案且不插值。
const noop = () => undefined
const cellLabels = { unassigned: '未归类', manual: '人工指定，采集不会覆盖' }

// —— 单元格 ——
const websiteCell = renderToStaticMarkup(
  <SupplierCategoryCell hasSupplier name="Pens" path="Office > Pens" source="website" labels={cellLabels} />,
)
assert.ok(websiteCell.includes('Pens'), '显示叶子名')
assert.ok(!websiteCell.includes('pos-products-source-mark'), '自动归类不带人工标记')

const manualCell = renderToStaticMarkup(
  <SupplierCategoryCell hasSupplier name="Pens" path="Office > Pens" source="manual" labels={cellLabels} />,
)
assert.ok(manualCell.includes('pos-products-source-mark'), '人工指定带编辑标记')
assert.ok(manualCell.includes('aria-label="人工指定，采集不会覆盖"'), '人工标记需可被读屏识别')

const warehouseCell = renderToStaticMarkup(
  <SupplierCategoryCell hasSupplier name="笔" path="文具 > 笔" source="warehouse" labels={cellLabels} />,
)
assert.ok(warehouseCell.includes('笔') && !warehouseCell.includes('pos-products-source-mark'), '200 随仓库分类只显示名称')

const unassignedCell = renderToStaticMarkup(<SupplierCategoryCell hasSupplier labels={cellLabels} />)
assert.ok(unassignedCell.includes('pos-products-supplier-category-empty') && unassignedCell.includes('未归类'), '有供应商未归类用禁用色提示')

const noSupplierCell = renderToStaticMarkup(<SupplierCategoryCell hasSupplier={false} labels={cellLabels} />)
assert.equal(noSupplierCell, '<span>-</span>', '无供应商显示占位符')

// —— 分类树节点标题 ——
const baseNode: LocalSupplierCategoryNode = {
  categoryGuid: 'c1',
  name: 'Clearance',
  depth: 0,
  isPromotional: true,
  promotionalSource: 'manual',
  isActive: true,
  productCount: 12,
  children: [],
}
const promoTitle = renderToStaticMarkup(
  <SupplierCategoryTreeNodeTitle node={baseNode} canManage onTogglePromotional={noop} />,
)
assert.ok(promoTitle.includes('Clearance') && promoTitle.includes('12'), '显示名称与商品数')
assert.ok(promoTitle.includes('促销') && promoTitle.includes('人工'), '促销标签标明人工来源')
assert.ok(promoTitle.includes('取消促销'), '有权限时可取消促销')

const readOnlyTitle = renderToStaticMarkup(
  <SupplierCategoryTreeNodeTitle node={{ ...baseNode, isPromotional: false, isActive: false }} canManage={false} onTogglePromotional={noop} />,
)
assert.ok(!readOnlyTitle.includes('设为促销') && !readOnlyTitle.includes('取消促销'), '无管理权限不显示切换链接')
assert.ok(readOnlyTitle.includes('已停用'), '停用节点有标记')
assert.ok(!readOnlyTitle.includes('ant-tag-orange'), '非促销不显示促销标签')

// —— 管理面板 ——
const summaries: LocalSupplierCategorySummary[] = [
  { supplierCode: '200', supplierName: 'Hot Bargain', sourceKind: 'warehouse', categoryCount: 50, promotionalCount: 0, productCount: 900, assignedCount: 900, manualCount: 0, unassignedCount: 0 },
  { supplierCode: '240', supplierName: 'DATS', sourceKind: 'website', categoryCount: 12, promotionalCount: 2, productCount: 300, assignedCount: 250, manualCount: 3, unassignedCount: 50, lastCapturedAt: '2026-09-23T01:02:03' },
  { supplierCode: '201', supplierName: 'Yatsal', sourceKind: 'website', categoryCount: 0, promotionalCount: 0, productCount: 40, assignedCount: 0, manualCount: 0, unassignedCount: 40 },
]
assert.deepEqual(filterWebsiteSupplierSummaries(summaries).map((summary) => summary.supplierCode), ['240', '201'])

const panelProps = {
  summaries,
  summaryLoading: false,
  onSelectSupplier: noop,
  pendingCategoryGuids: new Set<string>(),
  onTogglePromotional: noop,
  onRefresh: noop,
  onResolve: noop,
  resolving: false,
}
const managedPanel = renderToStaticMarkup(
  <SupplierCategoryManagerPanel
    {...panelProps}
    canManage
    selectedSupplierCode="240"
    treeEntry={{ status: 'ready', loaded: true, nodes: [] }}
  />,
)
assert.ok(!managedPanel.includes('data-supplier-code="200"'), '左侧列表过滤掉 200')
assert.ok(managedPanel.includes('Hot Bargain（200）的供应商分类即仓库分类'), '显示 200 的说明')
assert.ok(managedPanel.includes('data-supplier-code="240"') && managedPanel.includes('data-supplier-code="201"'))
assert.ok(/data-supplier-code="240"[^>]*aria-pressed="true"/.test(managedPanel) || /aria-pressed="true"[^>]*data-supplier-code="240"/.test(managedPanel), '选中供应商有 aria-pressed')
assert.ok(managedPanel.includes('250') && managedPanel.includes('300'), '显示已归类/商品数统计')
assert.ok(managedPanel.includes('2026-09-23 01:02'), '显示最近采集时间')
assert.ok(managedPanel.includes('尚未采集'), '未采集过的供应商有提示')
assert.ok(managedPanel.includes('重新解析'), '有管理权限显示重新解析')
assert.ok(managedPanel.includes('请在浏览器扩展中打开该供应商网站采集'), '空树引导去扩展采集')

const readOnlyPanel = renderToStaticMarkup(
  <SupplierCategoryManagerPanel {...panelProps} canManage={false} selectedSupplierCode="240" treeEntry={{ status: 'ready', loaded: true, nodes: [baseNode] }} />,
)
assert.ok(!readOnlyPanel.includes('重新解析'), '只读用户不显示重新解析')
assert.ok(readOnlyPanel.includes('刷新'), '只读用户可以刷新')

const emptyPanel = renderToStaticMarkup(
  <SupplierCategoryManagerPanel {...panelProps} summaries={[summaries[0]]} canManage />,
)
assert.ok(emptyPanel.includes('暂无供应商分类数据'), '只有 200 时显示空态')
assert.ok(emptyPanel.includes('请选择左侧供应商'))
assert.ok(!emptyPanel.includes('重新解析'), '没有选中供应商时不提供重新解析')

// —— 表单字段 ——
const noSupplierField = renderToStaticMarkup(<SupplierCategoryFormField onEnsureTree={noop} />)
assert.ok(noSupplierField.includes('请先选择澳洲供应商'), '无供应商时禁用并提示')
assert.ok(noSupplierField.includes('ant-select-disabled'), '无供应商时级联框禁用')

const hotBargainField = renderToStaticMarkup(
  <SupplierCategoryFormField supplierCode="200" warehousePath="文具 / 笔" onEnsureTree={noop} />,
)
assert.ok(hotBargainField.includes('随仓库分类') && hotBargainField.includes('文具 / 笔'), '200 只读展示仓库分类')
assert.ok(!hotBargainField.includes('ant-select'), '200 不提供选择器')

const manualField = renderToStaticMarkup(
  <SupplierCategoryFormField
    supplierCode="240"
    value="c1"
    originalGuid="c1"
    originalSource="manual"
    treeEntry={{ status: 'ready', loaded: true, nodes: [baseNode] }}
    onEnsureTree={noop}
  />,
)
assert.ok(manualField.includes('当前为人工指定'), '提示当前来源')
assert.ok(!manualField.includes('ant-select-disabled'), '已加载时可选择')

const clearedField = renderToStaticMarkup(
  <SupplierCategoryFormField
    supplierCode="240"
    originalGuid="c1"
    originalSource="website"
    treeEntry={{ status: 'ready', loaded: true, nodes: [baseNode] }}
    onEnsureTree={noop}
  />,
)
assert.ok(clearedField.includes('保存后将恢复按采集数据自动归类'), '清空后提示将恢复自动归类')

const loadingField = renderToStaticMarkup(
  <SupplierCategoryFormField supplierCode="240" treeEntry={{ status: 'loading', loaded: false, nodes: [] }} onEnsureTree={noop} />,
)
assert.ok(loadingField.includes('正在加载分类') && loadingField.includes('ant-select-disabled'), '加载中禁用')

const failedField = renderToStaticMarkup(
  <SupplierCategoryFormField supplierCode="240" treeEntry={{ status: 'error', loaded: false, nodes: [] }} onEnsureTree={noop} onRetryTree={noop} />,
)
assert.ok(failedField.includes('重试'), '加载失败提供重试')

const batchUnavailable = renderToStaticMarkup(
  <SupplierCategoryFormField mode="batch" unavailableReason="所选商品属于多个澳洲供应商" onEnsureTree={noop} />,
)
assert.ok(batchUnavailable.includes('所选商品属于多个澳洲供应商') && batchUnavailable.includes('ant-select-disabled'), '批量不可设置时禁用并说明原因')

console.log('supplierCategoryComponents.test: ok')
