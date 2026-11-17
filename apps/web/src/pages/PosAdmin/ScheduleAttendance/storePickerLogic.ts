// 排班考勤的分店列表（侧栏 / 收起后的弹层）与周切换：搜索、最近使用、初始分店、侧栏是否展开、相对周数。纯函数，便于单测。

export interface StorePickerOption {
  label: string
  value: string
}

/** 「最近使用」最多记几家。 */
export const RECENT_STORE_LIMIT = 3

/** 侧栏宽度 + 7 天看板最小宽度（7 × 132）+ 两侧留白：卡片宽度达到它才默认展开侧栏，否则 7 天放不进一屏。 */
export const STORE_SIDEBAR_WIDTH = 212
export const STORE_SIDEBAR_DOCK_MIN_WIDTH = STORE_SIDEBAR_WIDTH + 7 * 132 + 84

/**
 * 表格页签（考勤记录 / 员工可用时间 / 打卡记录 / 公共假期等）的展开阈值：侧栏 + 约 644 的表格可视宽度 + 两侧留白。
 * 比周排班放宽，因为这些表格本来就能横向滚动，不需要像 7 天看板那样一屏放下。
 */
export const STORE_SIDEBAR_TABLE_DOCK_MIN_WIDTH = STORE_SIDEBAR_WIDTH + 644 + 84

/** 记住「全部分店」用的占位值（分店编码不会是这个）。 */
export const ALL_STORES_TOKEN = '*'

export const STORAGE_KEYS = {
  lastStore: 'schedule-attendance:last-store',
  recentStores: 'schedule-attendance:recent-stores',
  sidebarDock: 'schedule-attendance:store-sidebar',
  /** 周视图员工名显示方式：nickname（默认）/ full。 */
  staffNameMode: 'schedule-attendance:staff-name-mode',
} as const

/** 侧栏偏好：docked＝固定在左侧，floating＝收起成按钮，null＝按宽度自动。 */
export type SidebarPreference = 'docked' | 'floating' | null

/** 按名称或编码搜索，忽略大小写和首尾空格。 */
export function filterStoreOptions<T extends StorePickerOption>(options: readonly T[], keyword: string): T[] {
  const text = keyword.trim().toLowerCase()
  if (!text) return [...options]
  return options.filter((option) => option.label.toLowerCase().includes(text) || option.value.toLowerCase().includes(text))
}

/** 选中一家后更新「最近使用」：放到最前、去重、最多 RECENT_STORE_LIMIT 家。 */
export function pushRecentStore(recent: readonly string[], storeCode: string | undefined, limit = RECENT_STORE_LIMIT): string[] {
  if (!storeCode) return [...recent].slice(0, limit)
  return [storeCode, ...recent.filter((code) => code !== storeCode)].slice(0, limit)
}

export interface StoreSections<T extends StorePickerOption> {
  /** 最近使用（只保留仍在可选列表里的，按使用先后）；搜索时为空。 */
  recent: T[]
  /** 全部分店（搜索时是搜索结果）。 */
  all: T[]
}

/** 列表分两段：最近使用 + 全部分店；有搜索词时只给搜索结果，不再分段。 */
export function buildStoreSections<T extends StorePickerOption>(
  options: readonly T[],
  recent: readonly string[],
  keyword: string,
): StoreSections<T> {
  if (keyword.trim()) return { recent: [], all: filterStoreOptions(options, keyword) }
  const byCode = new Map(options.map((option) => [option.value, option]))
  const recentOptions = recent.map((code) => byCode.get(code)).filter((option): option is T => Boolean(option))
  // 分店不多时「最近使用」意义不大，只有 1 家时也不显示
  return { recent: options.length > 4 ? recentOptions : [], all: [...options] }
}

/** 键盘上下移动后的高亮位置（首尾循环）；列表为空时返回 -1。 */
export function moveActiveIndex(current: number, delta: 1 | -1, length: number): number {
  if (length <= 0) return -1
  if (current < 0) return delta > 0 ? 0 : length - 1
  return (current + delta + length) % length
}

/**
 * 初始分店：上次记住且仍可选的优先；记住的是「全部分店」就保持不选；
 * 只有 1 家可选时直接选中它（否则批量排班、发布等按钮会因为没选分店而一直不可用）。
 */
export function resolveInitialStore(options: readonly StorePickerOption[], remembered: string | null | undefined): string | undefined {
  if (remembered && remembered !== ALL_STORES_TOKEN && options.some((option) => option.value === remembered)) return remembered
  if (options.length === 1) return options[0].value
  return undefined
}

/**
 * 侧栏是否展开：手机、只有 1 家分店时不显示；用户点过「固定 / 收起」按偏好；否则看卡片宽度。
 * dockMinWidth 缺省按周排班（7 天看板）的阈值；表格页签传 STORE_SIDEBAR_TABLE_DOCK_MIN_WIDTH。
 */
export function shouldDockSidebar(input: {
  phone: boolean
  storeCount: number
  containerWidth: number
  preference: SidebarPreference
  dockMinWidth?: number
}): boolean {
  if (input.phone || input.storeCount <= 1) return false
  if (input.preference) return input.preference === 'docked'
  return input.containerWidth >= (input.dockMinWidth ?? STORE_SIDEBAR_DOCK_MIN_WIDTH)
}

export function parseSidebarPreference(value: string | null | undefined): SidebarPreference {
  return value === 'docked' || value === 'floating' ? value : null
}

/** 读「最近使用」：存储内容损坏时当作空。 */
export function parseRecentStores(value: string | null | undefined): string[] {
  if (!value) return []
  try {
    const parsed: unknown = JSON.parse(value)
    return Array.isArray(parsed)
      ? parsed.filter((item): item is string => typeof item === 'string' && item.length > 0).slice(0, RECENT_STORE_LIMIT)
      : []
  } catch {
    return []
  }
}

const DAY_MS = 24 * 60 * 60 * 1000

/**
 * 选中周相对本周差几周（两边都传 ISO 周一的日期 YYYY-MM-DD）：0＝本周，1＝下 1 周，-2＝上 2 周。
 * 按 UTC 日期算，避开夏令时切换那周少一小时导致的取整误差。
 */
export function getRelativeWeekOffset(selectedWeekStart: string, currentWeekStart: string): number {
  const selected = Date.parse(`${selectedWeekStart}T00:00:00Z`)
  const current = Date.parse(`${currentWeekStart}T00:00:00Z`)
  if (Number.isNaN(selected) || Number.isNaN(current)) return 0
  return Math.round((selected - current) / (7 * DAY_MS))
}

// ---------- 本机记忆（localStorage）：只是便利功能，读写失败一律静默 ----------

export interface KeyValueStorage {
  getItem(key: string): string | null
  setItem(key: string, value: string): void
}

export function getBrowserStorage(): KeyValueStorage | null {
  try {
    return typeof window !== 'undefined' && window.localStorage ? window.localStorage : null
  } catch {
    return null
  }
}

export function safeStorageGet(storage: KeyValueStorage | null, key: string): string | null {
  if (!storage) return null
  try {
    return storage.getItem(key)
  } catch {
    return null
  }
}

export function safeStorageSet(storage: KeyValueStorage | null, key: string, value: string): void {
  if (!storage) return
  try {
    storage.setItem(key, value)
  } catch {
    // 配额满、隐私模式：忽略
  }
}
