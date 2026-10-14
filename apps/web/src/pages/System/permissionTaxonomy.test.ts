import assert from 'node:assert/strict'

import {
  ALL_GROUPS_KEY,
  PERMISSION_TAXONOMY,
  buildTaxonomyNav,
  categoriesOfGroup,
  matchesPermissionSurface,
  resolveActiveGroupKey,
  resolvePermissionSurfaces,
} from './permissionTaxonomy'

// ---- 使用端解析 ----
assert.deepEqual(resolvePermissionSurfaces('Users.View', ['mobile', 'web']), ['web', 'mobile'], '按固定顺序输出：收银端、后台、移动端')
assert.deepEqual(resolvePermissionSurfaces('Permissions.PosTerminal.Sales.AddItem'), ['pos'], '旧后端没有字段：收银端前缀兜底为收银端')
assert.deepEqual(resolvePermissionSurfaces('Users.View'), ['web'], '旧后端没有字段：其余兜底为后台')
assert.deepEqual(resolvePermissionSurfaces('Users.View', []), ['web'], '空数组视同没有字段')
assert.deepEqual(resolvePermissionSurfaces('Users.View', ['tablet']), ['web'], '不认识的使用端被忽略后兜底')
assert.equal(matchesPermissionSurface(['web', 'mobile'], 'mobile'), true)
assert.equal(matchesPermissionSurface(['web'], 'mobile'), false)
assert.equal(matchesPermissionSurface(['pos'], 'all'), true)

// ---- 分类映射：每个分类名只能出现在一个分组里，否则导航计数会重复 ----
const seen = new Map<string, string>()
for (const section of PERMISSION_TAXONOMY) {
  for (const group of section.groups) {
    for (const category of group.categories) {
      assert.equal(seen.has(category), false, `分类「${category}」同时出现在 ${seen.get(category)} 与 ${group.key}`)
      seen.set(category, group.key)
    }
  }
}
assert.equal(seen.get('POS 钱箱'), 'pos-device', '设置 · 钱箱 · 小票 · 客显 合并成一组')
assert.equal(seen.get('POS 同步'), 'pos-admin', '管理 · 同步 · 审计 合并成一组')

// ---- 导航构建 ----
const entries = [
  { category: 'POS 销售', granted: true },
  { category: 'POS 销售', granted: true },
  { category: 'POS 钱箱', granted: false },
  { category: 'POS 小票', granted: true },
  { category: '排班考勤', granted: true },
  { category: '仓库管理', granted: false },
  { category: '用户管理', granted: false },
  { category: '全新的分类', granted: true },
]
const nav = buildTaxonomyNav(entries)
assert.deepEqual(nav.map((section) => section.key), ['pos', 'business', 'system', 'other'], '分区顺序固定，没有内容的分区不出现')
const pos = nav[0]
assert.deepEqual(pos.groups.map((group) => group.key), ['pos-sales', 'pos-device'], '分组按预设顺序，没有内容的分组不出现')
assert.deepEqual([pos.groups[1].total, pos.groups[1].granted], [2, 1], '多个分类合并后数量累加')
assert.deepEqual([pos.total, pos.granted], [4, 3], '分区统计是分组之和')
assert.deepEqual(nav[1].groups.map((group) => group.key), ['biz-warehouse', 'biz-store'], '业务分区内按预设顺序（仓库与采购在分店运营之前）')
const other = nav[3]
assert.deepEqual(other.groups.map((group) => [group.key, group.label, group.builtIn]), [['other:全新的分类', '全新的分类', false]], '没登记过的新分类自动落入「其他」，不会丢')
assert.deepEqual(buildTaxonomyNav([]), [], '没有条目时导航为空')

// ---- 选中分组 ----
assert.equal(resolveActiveGroupKey(null, nav), 'pos-sales', '没选过时落到第一个有内容的分组')
assert.equal(resolveActiveGroupKey('biz-store', nav), 'biz-store')
assert.equal(resolveActiveGroupKey('pos-return', nav), 'pos-sales', '选中的分组被搜索清空后回到第一个有内容的分组')
assert.equal(resolveActiveGroupKey(ALL_GROUPS_KEY, nav), ALL_GROUPS_KEY, '「全部」始终保留')
assert.equal(resolveActiveGroupKey('pos-sales', []), ALL_GROUPS_KEY, '导航为空时退回「全部」')
assert.deepEqual([...(categoriesOfGroup('pos-device', nav) ?? [])].sort(), ['POS 小票', 'POS 客显', 'POS 设置', 'POS 钱箱'].sort(), '分组展开成它包含的全部后端分类')
assert.equal(categoriesOfGroup(ALL_GROUPS_KEY, nav), null, '「全部」不限制分类')
assert.deepEqual([...(categoriesOfGroup('other:全新的分类', nav) ?? [])], ['全新的分类'])

console.log('permissionTaxonomy tests passed')
