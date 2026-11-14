// 排班周视图「一人一色」：给员工分配固定的浅色。
// 规则：同一员工始终得到同一种颜色（由员工 ID 哈希决定起点）；同一批员工里两人撞色时，
// 按姓名顺序让后者顺延到下一种空闲颜色，保证一批人（≤ 色板数量）互不重复。
// 红、橙不进色板：留给「已取消 / 冲突 / 不能上班」这些状态，避免和员工色混淆。

export interface StaffColor {
  /** 卡片底色（浅）。 */
  bg: string
  /** 左侧色条、图例圆点、边框（饱和）。 */
  bar: string
  /** 文字色（深）。 */
  ink: string
}

export const STAFF_PALETTE: readonly StaffColor[] = [
  { bg: '#e8f0ff', bar: '#3b82f6', ink: '#1d4ed8' }, // 蓝
  { bg: '#e3f6e9', bar: '#22a55a', ink: '#15803d' }, // 绿
  { bg: '#fbe8fb', bar: '#c026d3', ink: '#a21caf' }, // 洋红
  { bg: '#eceff4', bar: '#64748b', ink: '#334155' }, // 石板灰
  { bg: '#fdf6d3', bar: '#c9a000', ink: '#7a6200' }, // 黄
  { bg: '#dcf5f2', bar: '#14b8a6', ink: '#0f766e' }, // 青
  { bg: '#eef8d9', bar: '#7cb518', ink: '#4d7c0f' }, // 青柠
  { bg: '#e9e9ff', bar: '#6366f1', ink: '#4338ca' }, // 靛
  { bg: '#f5ece2', bar: '#b7791f', ink: '#92400e' }, // 棕
  { bg: '#ffe7f0', bar: '#ec4899', ink: '#be185d' }, // 粉
  { bg: '#def3fd', bar: '#0ea5e9', ink: '#0369a1' }, // 天蓝
  { bg: '#f3f1dc', bar: '#9c8f2b', ink: '#6b6114' }, // 橄榄
  { bg: '#f1eaff', bar: '#8b5cf6', ink: '#6d28d9' }, // 紫
  { bg: '#e9f1fb', bar: '#3567b5', ink: '#1e3a8a' }, // 藏青
  { bg: '#eef6e8', bar: '#4f8a10', ink: '#2f5d0a' }, // 苔绿
  { bg: '#f7e9f5', bar: '#9d3b8f', ink: '#6b1f61' }, // 梅
]

/** 找不到员工（例如已离职但仍有历史班次）时用的中性灰。 */
export const FALLBACK_STAFF_COLOR: StaffColor = { bg: '#f2f3f5', bar: '#8c8c8c', ink: '#434343' }

/** FNV-1a 32 位哈希：同一个字符串永远得到同一个数，跨设备、跨周一致。 */
export function hashString(value: string): number {
  let hash = 0x811c9dc5
  for (let index = 0; index < value.length; index += 1) {
    hash ^= value.charCodeAt(index)
    hash = Math.imul(hash, 0x01000193) >>> 0
  }
  return hash >>> 0
}

export interface ColorSubject {
  userGuid: string
  name?: string
}

const collator = new Intl.Collator(undefined, { sensitivity: 'base', numeric: true })

/**
 * 给一批员工分配颜色。入参顺序不影响结果（内部按姓名、ID 排序后再分配）；
 * 员工数超过色板数量时，多出来的人只能复用颜色（仍由各自的哈希决定）。
 */
export function assignStaffColors(subjects: readonly ColorSubject[]): Map<string, StaffColor> {
  const unique = new Map<string, ColorSubject>()
  subjects.forEach((item) => {
    if (item.userGuid && !unique.has(item.userGuid)) unique.set(item.userGuid, item)
  })
  const ordered = Array.from(unique.values()).sort(
    (left, right) => collator.compare(left.name ?? '', right.name ?? '') || left.userGuid.localeCompare(right.userGuid),
  )

  const size = STAFF_PALETTE.length
  const used = new Set<number>()
  const result = new Map<string, StaffColor>()
  ordered.forEach((subject) => {
    let index = hashString(subject.userGuid) % size
    if (used.size < size) {
      // 还有空闲颜色：从哈希位置起向后找第一个没人用的
      while (used.has(index)) index = (index + 1) % size
    }
    used.add(index)
    result.set(subject.userGuid, STAFF_PALETTE[index])
  })
  return result
}
