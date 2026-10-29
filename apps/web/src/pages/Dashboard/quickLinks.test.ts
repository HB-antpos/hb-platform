import {
  DEFAULT_QUICK_LINK_KEYS,
  QUICK_LINK_LIMIT,
  defaultPinnedKeys,
  filterLinks,
  flattenMenuLinks,
  getGreetingPeriod,
  groupLinksByModule,
  moveKey,
  pinKey,
  resolvePinnedKeys,
  unpinKey,
} from './quickLinks'

function assert(condition: unknown, message: string) {
  if (!condition) throw new Error(message)
}

function leaf(key: string, label: string) {
  return { key, label, icon: `icon:${key}` }
}

// 与真实侧栏同构：一级菜单带子菜单，另有工作台、一级叶子菜单、分割线和非字符串标题
const menu = [
  { key: '/dashboard', label: '工作台', icon: 'icon:dashboard' },
  {
    key: '/pos-admin',
    label: '收银管理',
    icon: 'icon:pos',
    children: [
      leaf('/pos-admin/attendance', '排班考勤'),
      leaf('/pos-admin/payroll', '工时与工资'),
      { key: '/pos-admin/no-icon', label: '无图标页' },
      { key: '/pos-admin/badge', label: { custom: true } },
      { type: 'divider' },
    ],
  },
  {
    key: '/warehouse',
    label: '仓库管理',
    icon: 'icon:wh',
    children: [
      leaf('/warehouse/containers', '货柜管理'),
      { key: '/warehouse/nested', label: '二级菜单', children: [leaf('/warehouse/nested/deep', '深层页面')] },
    ],
  },
  { key: '/seasonal-preorders/ordering', label: '节日订货', icon: 'icon:season' },
  null,
]

const entries = flattenMenuLinks(menu as never)
const keys = entries.map((entry) => entry.key)

// flattenMenuLinks
assert(!keys.includes('/dashboard'), '工作台不算常用导航候选')
assert(!keys.includes('/pos-admin/badge'), '标题不是字符串的节点跳过')
assert(
  keys.join(',') ===
    '/pos-admin/attendance,/pos-admin/payroll,/pos-admin/no-icon,/warehouse/containers,/warehouse/nested/deep,/seasonal-preorders/ordering',
  `叶子页面应按菜单顺序拍平：${keys.join(',')}`,
)
assert(entries[0].module === '收银管理' && entries[0].moduleKey === '/pos-admin', '叶子页面记录所属一级菜单')
assert(entries[2].icon === 'icon:pos', '叶子没有图标时回退到一级菜单图标')
assert(entries[4].module === '仓库管理', '多级嵌套仍归属最上层一级菜单')
assert(entries[5].module === '节日订货', '一级叶子菜单的所属模块是它自己')
assert(flattenMenuLinks(undefined).length === 0, '空菜单')
assert(
  flattenMenuLinks([leaf('/a', 'A'), leaf('/a', 'A 重复')] as never).length === 1,
  '重复 key 只保留第一个',
)

// groupLinksByModule
const modules = groupLinksByModule(entries)
assert(modules.map((item) => item.title).join(',') === '收银管理,仓库管理,节日订货', '按一级菜单分组且保持顺序')
assert(modules[0].links.length === 3 && modules[1].links.length === 2, '各组页面数')

// filterLinks
assert(filterLinks(entries, '  ').length === entries.length, '空关键字返回全部')
assert(filterLinks(entries, '货柜').map((item) => item.key).join() === '/warehouse/containers', '按标题搜索')
assert(filterLinks(entries, '仓库').length === 2, '按所属模块搜索')
assert(filterLinks(entries, 'ZZZ').length === 0, '无匹配')

// defaultPinnedKeys：默认路径里有权限的优先，不够 6 个用菜单靠前页面补
{
  const fullAccess = DEFAULT_QUICK_LINK_KEYS.map((key) => leaf(key, key))
  const defaults = defaultPinnedKeys(flattenMenuLinks(fullAccess as never))
  assert(defaults.join() === DEFAULT_QUICK_LINK_KEYS.join(), '全有权限时按默认顺序')

  const limited = defaultPinnedKeys(entries)
  assert(limited.includes('/pos-admin/attendance') && limited.includes('/warehouse/containers'), '有权限的默认页面优先')
  assert(limited.length === 6, `默认页面不足时补到 6 个，实际 ${limited.length}`)
  assert(new Set(limited).size === limited.length, '补位不重复')
  assert(defaultPinnedKeys(entries, 3).length === 3, '遵守上限')
}

// resolvePinnedKeys：undefined 走默认，数组按存储顺序并过滤失效项
assert(
  resolvePinnedKeys(entries, undefined).join() === defaultPinnedKeys(entries).join(),
  '没存过时使用默认',
)
assert(resolvePinnedKeys(entries, []).length === 0, '空数组表示用户主动清空，不回退默认')
assert(
  resolvePinnedKeys(entries, ['/warehouse/containers', '/gone', '/pos-admin/payroll', '/warehouse/containers']).join() ===
    '/warehouse/containers,/pos-admin/payroll',
  '过滤失去权限的页面、去重、保持存储顺序',
)
assert(
  resolvePinnedKeys(entries, keys, 2).length === 2,
  '解析结果不超过上限',
)

// pinKey / unpinKey / moveKey
{
  const base = ['a', 'b']
  assert(pinKey(base, 'c').join() === 'a,b,c', '固定追加到末尾')
  assert(pinKey(base, 'a') === base, '重复固定原样返回')
  const full = Array.from({ length: QUICK_LINK_LIMIT }, (_, index) => `k${index}`)
  assert(pinKey(full, 'extra') === full, '达到上限不能再固定')
  assert(unpinKey(base, 'a').join() === 'b', '取消固定')
  assert(unpinKey(base, 'x') === base, '取消不存在的项原样返回')
  assert(moveKey(['a', 'b', 'c'], 'a', 'c').join() === 'b,c,a', '向后拖')
  assert(moveKey(['a', 'b', 'c'], 'c', 'a').join() === 'c,a,b', '向前拖')
  const same = ['a', 'b']
  assert(moveKey(same, 'a', 'x') === same, '目标不在列表原样返回')
  assert(moveKey(same, 'a', 'a') === same, '原地不动')
}

// getGreetingPeriod
assert(getGreetingPeriod(5) === 'morning' && getGreetingPeriod(11) === 'morning', '早上')
assert(getGreetingPeriod(12) === 'afternoon' && getGreetingPeriod(17) === 'afternoon', '下午')
assert(getGreetingPeriod(18) === 'evening' && getGreetingPeriod(2) === 'evening', '晚上与凌晨')

console.log('quickLinks tests passed')
