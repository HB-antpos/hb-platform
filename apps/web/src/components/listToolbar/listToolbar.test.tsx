import assert from 'node:assert/strict'
import { renderToStaticMarkup } from 'react-dom/server'
import ActiveFilterBar from './ActiveFilterBar'
import SelectionActionBar from './SelectionActionBar'
import StatusPill from './StatusPill'
import StatusTabs from './StatusTabs'
import ToolbarMenuButton from './ToolbarMenuButton'

const noop = () => undefined

// 勾选后操作条：没有选中行时整条不渲染，不占位置。
assert.equal(
  renderToStaticMarkup(
    <SelectionActionBar selectedCount={0} onClearSelection={noop}>
      <button type="button">批量上架</button>
    </SelectionActionBar>,
  ),
  '',
  '未选中任何行时不应渲染操作条',
)
const selectionMarkup = renderToStaticMarkup(
  <SelectionActionBar selectedCount={2} onClearSelection={noop}>
    <button type="button">批量上架</button>
  </SelectionActionBar>,
)
assert.ok(selectionMarkup.includes('批量上架'), '有选中行时应渲染传入的操作按钮')
assert.ok(selectionMarkup.includes('aria-live="polite"'), '选中数量变化应能被读屏软件播报')

// 菜单按钮：所有项都无权限时不渲染，避免出现空菜单。
assert.equal(
  renderToStaticMarkup(
    <ToolbarMenuButton
      label="同步"
      actions={[
        { key: 'a', label: '从HQ同步库存', visible: false, onClick: noop },
        { key: 'b', label: '更新分店价格', visible: false, onClick: noop },
      ]}
    />,
  ),
  '',
  '全部菜单项不可见时不应渲染按钮',
)
assert.ok(
  renderToStaticMarkup(
    <ToolbarMenuButton label="同步" actions={[{ key: 'a', label: '从HQ同步库存', onClick: noop }]} />,
  ).includes('同步'),
  '至少一项可见时应渲染菜单按钮',
)

// 任务进行中：只转图标，不能进入 antd 的 loading 态（该态会吞掉点击，菜单就打不开了）。
const loadingMenuMarkup = renderToStaticMarkup(
  <ToolbarMenuButton label="同步" loading actions={[{ key: 'a', label: '更新分店价格', onClick: noop }]} />,
)
assert.ok(!loadingMenuMarkup.includes('ant-btn-loading'), '菜单按钮不应进入 antd loading 态')
assert.ok(loadingMenuMarkup.includes('aria-busy="true"'), '任务进行中应标记 aria-busy')
assert.ok(loadingMenuMarkup.includes('anticon-loading'), '任务进行中应显示转圈图标')

// 已生效筛选条：无条件时显示提示；有条件时每个条件带移除按钮，列头来源有标记。
const emptyMarkup = renderToStaticMarkup(<ActiveFilterBar items={[]} onClearAll={noop} />)
assert.ok(!emptyMarkup.includes('<button'), '无条件时不应出现移除或清空按钮')

const chipsMarkup = renderToStaticMarkup(
  <ActiveFilterBar
    items={[
      { key: 'category', label: '分类', value: '跑马节帽子', source: 'toolbar', onRemove: noop },
      { key: 'retailPrice', label: '零售价', value: '≥ 5.00', source: 'column', onRemove: noop },
    ]}
    onClearAll={noop}
  />,
)
assert.ok(chipsMarkup.includes('跑马节帽子') && chipsMarkup.includes('≥ 5.00'), '应显示每个条件的值')
// antd 图标自带 aria-label，因此按移除按钮自身计数，并确认按钮带有指明条件名的无障碍标签。
assert.equal((chipsMarkup.match(/class="list-toolbar-chip-remove"/g) ?? []).length, 2, '每个条件应有一个移除按钮')
// 测试环境未初始化 i18n，react-i18next 直接返回默认文案且不插值，这里只校验标签存在。
assert.ok(chipsMarkup.includes('aria-label="移除筛选条件：'), '移除按钮应带无障碍标签')
assert.ok(chipsMarkup.includes('list-toolbar-chip-column'), '列头来源的条件应有区分样式')

// 状态页签：当前项 aria-pressed，计数按千分位显示；未知计数不显示数字。
const tabsMarkup = renderToStaticMarkup(
  <StatusTabs
    ariaLabel="订单状态"
    activeKey="active"
    onChange={noop}
    items={[
      { key: 'active', label: '进行中', count: 46 },
      { key: 'done', label: '已完成', count: 1238 },
      { key: 'all', label: '全部' },
      { key: 'overdue', label: '逾期', count: 3, tone: 'danger' },
    ]}
  />,
)
assert.equal((tabsMarkup.match(/aria-pressed="true"/g) ?? []).length, 1, '只应有一个当前页签')
assert.ok(tabsMarkup.includes('aria-label="订单状态"'), '页签组应带无障碍名称')
assert.ok(tabsMarkup.includes('1,238'), '计数应按千分位显示')
assert.equal((tabsMarkup.match(/list-toolbar-status-count /g) ?? []).length, 3, '未传计数的页签不应显示计数')
assert.ok(tabsMarkup.includes('list-toolbar-status-count-danger'), '异常计数应有强调样式')

// 状态胶囊：按色调输出类名，圆点对读屏隐藏。
const pillMarkup = renderToStaticMarkup(<StatusPill tone="orange">配货中</StatusPill>)
assert.ok(pillMarkup.includes('list-toolbar-pill-orange') && pillMarkup.includes('配货中'), '应按色调渲染状态文字')
assert.ok(pillMarkup.includes('aria-hidden="true"'), '圆点应对读屏隐藏')

console.log('listToolbar.test: ok')
