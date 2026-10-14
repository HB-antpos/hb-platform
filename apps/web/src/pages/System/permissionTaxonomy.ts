/**
 * 权限分类导航与「使用端」：角色管理页与权限管理页共用的纯逻辑。
 *
 * 分类名（如 `POS 销售`、`排班考勤`）由后端权限表决定，一共 30+ 个；
 * 这里只在前端加一层「分区 → 分组」的归并来组织导航，不改库里的分类值。
 * 没出现在映射里的新分类会自动落到「其他」分区，不会丢失。
 */

export type PermissionSurface = 'pos' | 'web' | 'mobile'
export type PermissionSurfaceFilter = 'all' | PermissionSurface

/** 固定顺序：收银端、后台、移动端（徽标与筛选都按这个顺序展示）。 */
export const PERMISSION_SURFACES: readonly PermissionSurface[] = ['pos', 'web', 'mobile']

const POS_TERMINAL_PREFIX = 'Permissions.PosTerminal.'

/**
 * 解析权限的使用端。后端会带 `surfaces`；旧后端（部署先后差异）没有该字段时，
 * 只能凭收银端前缀兜底：Permissions.PosTerminal.* 是收银端，其余按后台。
 */
export function resolvePermissionSurfaces(
  code: string,
  surfaces?: readonly string[] | null,
): PermissionSurface[] {
  const known = PERMISSION_SURFACES.filter((surface) => surfaces?.includes(surface))
  if (known.length > 0) return known
  return code.startsWith(POS_TERMINAL_PREFIX) ? ['pos'] : ['web']
}

export function matchesPermissionSurface(
  surfaces: readonly PermissionSurface[],
  filter: PermissionSurfaceFilter,
): boolean {
  return filter === 'all' || surfaces.includes(filter)
}

export type TaxonomySectionKey = 'pos' | 'business' | 'system' | 'other'

interface TaxonomyGroupDefinition {
  key: string
  /** 中文默认文案；实际显示走 i18n：system.permissionTaxonomy.groups.<key> */
  label: string
  /** 归入该分组的后端分类名 */
  categories: readonly string[]
}

interface TaxonomySectionDefinition {
  key: Exclude<TaxonomySectionKey, 'other'>
  label: string
  groups: readonly TaxonomyGroupDefinition[]
}

export const PERMISSION_TAXONOMY: readonly TaxonomySectionDefinition[] = [
  {
    key: 'pos',
    label: '收银端 POS',
    groups: [
      { key: 'pos-sales', label: '销售', categories: ['POS 销售'] },
      { key: 'pos-payment', label: '收款', categories: ['POS 收款'] },
      { key: 'pos-return', label: '退货', categories: ['POS 退货'] },
      { key: 'pos-special', label: '特价商品', categories: ['POS 特价商品'] },
      { key: 'pos-installment', label: '分期', categories: ['POS 分期'] },
      { key: 'pos-daily-close', label: '日结', categories: ['POS 日结'] },
      { key: 'pos-history', label: '历史订单', categories: ['POS 历史订单'] },
      {
        key: 'pos-device',
        label: '设置 · 钱箱 · 小票 · 客显',
        categories: ['POS 设置', 'POS 钱箱', 'POS 小票', 'POS 客显'],
      },
      { key: 'pos-admin', label: '管理 · 同步 · 审计', categories: ['POS 管理', 'POS 同步', 'POS 审计'] },
    ],
  },
  {
    key: 'business',
    label: '后台 · 业务',
    groups: [
      {
        key: 'biz-product',
        label: '商品与价格',
        categories: ['商品管理', '价格管理', '定价策略', '促销管理', '分店商品管理'],
      },
      {
        key: 'biz-warehouse',
        label: '仓库与采购',
        categories: ['仓库管理', '货柜管理', '国内采购', '澳洲供应商', '本地进货管理', '前台订货', '分店订货'],
      },
      {
        key: 'biz-store',
        label: '分店运营',
        categories: ['分店管理', '分店运营', '分店财务', '排班考勤', '季节卡片'],
      },
      { key: 'biz-report', label: '报表与营销', categories: ['销售看板', '报表', '订单管理', '广告管理'] },
    ],
  },
  {
    key: 'system',
    label: '系统与人员',
    groups: [
      { key: 'sys-access', label: '用户 · 角色 · 系统', categories: ['用户管理', '角色管理', '系统管理', '后台管理'] },
    ],
  },
]

export const OTHER_GROUP_PREFIX = 'other:'
export const ALL_GROUPS_KEY = 'all'

const CATEGORY_TO_GROUP = new Map<string, { sectionKey: TaxonomySectionKey; group: TaxonomyGroupDefinition }>(
  PERMISSION_TAXONOMY.flatMap((section) =>
    section.groups.flatMap((group) =>
      group.categories.map((category) => [category, { sectionKey: section.key, group }] as const),
    ),
  ),
)

export interface TaxonomyEntry {
  category: string
  granted?: boolean
}

export interface TaxonomyNavGroup {
  key: string
  /** 中文默认文案（「其他」分区里就是分类原名） */
  label: string
  /** 是否是内置分组（内置分组才有 i18n 文案，「其他」分区用分类原名） */
  builtIn: boolean
  categories: string[]
  total: number
  granted: number
}

export interface TaxonomyNavSection {
  key: TaxonomySectionKey
  label: string
  groups: TaxonomyNavGroup[]
  total: number
  granted: number
}

/** 按当前可见的权限条目生成导航：只保留有条目的分组，数量与右侧列表一致。 */
export function buildTaxonomyNav(entries: readonly TaxonomyEntry[]): TaxonomyNavSection[] {
  const groups = new Map<string, TaxonomyNavGroup>()
  const sectionOfGroup = new Map<string, TaxonomySectionKey>()

  for (const entry of entries) {
    const known = CATEGORY_TO_GROUP.get(entry.category)
    const key = known ? known.group.key : `${OTHER_GROUP_PREFIX}${entry.category}`
    let group = groups.get(key)
    if (!group) {
      group = {
        key,
        label: known ? known.group.label : entry.category,
        builtIn: Boolean(known),
        categories: known ? [...known.group.categories] : [entry.category],
        total: 0,
        granted: 0,
      }
      groups.set(key, group)
      sectionOfGroup.set(key, known ? known.sectionKey : 'other')
    }
    group.total += 1
    if (entry.granted) group.granted += 1
  }

  const sections: TaxonomyNavSection[] = []
  const sectionDefinitions: { key: TaxonomySectionKey; label: string; order: readonly string[] }[] = [
    ...PERMISSION_TAXONOMY.map((section) => ({
      key: section.key as TaxonomySectionKey,
      label: section.label,
      order: section.groups.map((group) => group.key),
    })),
    { key: 'other', label: '其他', order: [] },
  ]

  for (const definition of sectionDefinitions) {
    const members = [...groups.values()].filter((group) => sectionOfGroup.get(group.key) === definition.key)
    if (members.length === 0) continue
    // 内置分组按预设顺序；「其他」分区按分类名排序，保证稳定。
    members.sort((a, b) =>
      definition.key === 'other'
        ? a.label.localeCompare(b.label, 'zh-Hans-CN')
        : definition.order.indexOf(a.key) - definition.order.indexOf(b.key),
    )
    sections.push({
      key: definition.key,
      label: definition.label,
      groups: members,
      total: members.reduce((sum, group) => sum + group.total, 0),
      granted: members.reduce((sum, group) => sum + group.granted, 0),
    })
  }
  return sections
}

/**
 * 解析当前选中的分组：用户选了「全部」就是全部；选中的分组已不存在（被搜索/筛选清空）
 * 或还没选过时，落到第一个有内容的分组，避免右侧出现空白。
 */
export function resolveActiveGroupKey(
  requested: string | null,
  sections: readonly TaxonomyNavSection[],
): string {
  if (requested === ALL_GROUPS_KEY) return ALL_GROUPS_KEY
  const keys = sections.flatMap((section) => section.groups.map((group) => group.key))
  if (requested && keys.includes(requested)) return requested
  return keys[0] ?? ALL_GROUPS_KEY
}

/** 选中分组包含的后端分类名；选「全部」返回 null 表示不限制。 */
export function categoriesOfGroup(
  activeKey: string,
  sections: readonly TaxonomyNavSection[],
): ReadonlySet<string> | null {
  if (activeKey === ALL_GROUPS_KEY) return null
  const group = sections.flatMap((section) => section.groups).find((item) => item.key === activeKey)
  return group ? new Set(group.categories) : null
}
