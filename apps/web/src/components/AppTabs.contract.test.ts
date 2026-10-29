import { readFileSync } from 'node:fs'
import { join } from 'node:path'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

// 页签条的交互契约：整个页签可拖（不再有左侧手柄）、右键菜单、锁定页签不可关闭。
// 这些是源码级约束，防止后续改动把手柄 / 键盘排序退回旧实现。
const root = process.cwd()
const appTabsSource = readFileSync(join(root, 'src/components/AppTabs.tsx'), 'utf8')
const cssSource = readFileSync(join(root, 'src/styles/global.css'), 'utf8')
const storeSource = readFileSync(join(root, 'src/store/tabs.ts'), 'utf8')

assert(!appTabsSource.includes('HolderOutlined'), '页签不应再渲染左侧拖动手柄图标')
assert(!appTabsSource.includes('app-tab-drag-handle'), '页签不应再使用拖动手柄')
assert(!cssSource.includes('app-tab-drag-handle'), '拖动手柄样式应已删除')
assert(!/\.app-tab-sortable-node \.ant-tabs-tab \{\s*padding-left: 32px/.test(cssSource), '不应再为手柄预留左侧内边距')

assert(appTabsSource.includes('{...listeners}'), '拖拽监听应绑在整个页签节点上')
assert(!appTabsSource.includes('setActivatorNodeRef'), '不再指定单独的拖动激活节点')
assert(appTabsSource.includes("trigger={['contextMenu']}"), '页签应支持右键菜单')
assert(appTabsSource.includes('closable: tab.closable !== false && !locked'), '锁定页签不显示关闭按钮')

// 锁定页签受保护：单个关闭与三种批量关闭都经过锁定判断
assert(storeSource.includes('isLockedTab(target)'), 'removeTab 必须拒绝锁定页签')
assert(
  ['others', 'left', 'right'].every((scope) => storeSource.includes(`removeTabsByScope(get, set, key, '${scope}')`)),
  '批量关闭应统一走 removeTabsByScope（跳过固定与锁定页签）',
)

console.log('AppTabs contract tests passed')
