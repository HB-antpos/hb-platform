// 排班看板的员工显示名：默认显示昵称，没填就用「名」兜底，可切换成全名。纯函数，便于单测。
// 与后端 PersonNameFormatter.ShortName 同一套顺序：昵称 → 名 → 全名 → 登录名。
// 只用于看板（图例 + 班次卡片）；批量排班弹窗、选人下拉、搜索仍用全名，避免选错人。

import { STORAGE_KEYS, safeStorageGet, safeStorageSet } from './storePickerLogic'
import type { KeyValueStorage } from './storePickerLogic'

/** nickname＝昵称优先（默认）；full＝全名。 */
export type StaffNameMode = 'nickname' | 'full'

export const DEFAULT_STAFF_NAME_MODE: StaffNameMode = 'nickname'

export interface StaffNameSource {
  userGuid: string
  username?: string | null
  fullName?: string | null
  givenName?: string | null
  familyName?: string | null
  nickname?: string | null
}

/** 去首尾空白；空白串当作没填。 */
function clean(value: string | null | undefined): string | undefined {
  const text = (value ?? '').trim()
  return text === '' ? undefined : text
}

/** 单个员工的显示名。全名模式与改动前一致（fullName → username → userGuid）。 */
export function resolveStaffName(source: StaffNameSource, mode: StaffNameMode): string {
  const fullName = clean(source.fullName)
  const username = clean(source.username)
  if (mode === 'full') return fullName ?? username ?? source.userGuid
  return clean(source.nickname) ?? clean(source.givenName) ?? fullName ?? username ?? source.userGuid
}

/** 姓的首字母：拉丁字母补「X.」，其它文字（中文等）直接用完整的姓。 */
function familyTag(familyName: string | null | undefined): string | undefined {
  const family = clean(familyName)
  if (!family) return undefined
  const first = Array.from(family)[0]
  return /^[A-Za-z]$/.test(first) ? `${first.toUpperCase()}.` : family
}

function groupByLowerName(names: Map<string, string>): Map<string, string[]> {
  const groups = new Map<string, string[]>()
  names.forEach((name, userGuid) => {
    const key = name.toLowerCase()
    groups.set(key, [...(groups.get(key) ?? []), userGuid])
  })
  return groups
}

/**
 * 一批员工的显示名（userGuid → 名字）。
 * 昵称模式下两人显示名相同（例如两个 Sam）时自动区分：先补姓的首字母（Sam C. / Sam L.），
 * 补完仍重复（同姓首字母、没有姓、与别人撞名）的再补登录名后缀（Sam (sam.chen)）；
 * 已经是全名兜底的（名字本身就带姓）不再补首字母。全名模式保持原样，不做处理。
 */
export function resolveStaffNames(sources: readonly StaffNameSource[], mode: StaffNameMode): Map<string, string> {
  const names = new Map<string, string>()
  const sourceByGuid = new Map<string, StaffNameSource>()
  sources.forEach((source) => {
    names.set(source.userGuid, resolveStaffName(source, mode))
    sourceByGuid.set(source.userGuid, source)
  })
  if (mode === 'full') return names

  const baseNames = new Map(names)

  // 第一轮：重名的补姓的首字母
  groupByLowerName(baseNames).forEach((guids) => {
    if (guids.length < 2) return
    guids.forEach((guid) => {
      const source = sourceByGuid.get(guid)
      const base = baseNames.get(guid)
      if (!source || !base) return
      if (base === clean(source.fullName)) return // 已经是全名，再补姓只会重复
      const tag = familyTag(source.familyName)
      if (tag) names.set(guid, `${base} ${tag}`)
    })
  })

  // 第二轮：补完仍重复的，补登录名后缀（登录名全局唯一）
  groupByLowerName(names).forEach((guids) => {
    if (guids.length < 2) return
    guids.forEach((guid) => {
      const source = sourceByGuid.get(guid)
      const base = baseNames.get(guid)
      if (!source || !base) return
      const suffix = clean(source.username) ?? guid.slice(0, 4)
      names.set(guid, `${base} (${suffix})`)
    })
  })

  return names
}

// ---------- 本机记忆（localStorage）：只是便利功能，读写失败一律静默 ----------

/** 读回的值只认 'full'，其余（没存过、被改坏）一律回到默认的昵称。 */
export function parseStaffNameMode(raw: string | null | undefined): StaffNameMode {
  return raw === 'full' ? 'full' : DEFAULT_STAFF_NAME_MODE
}

export function readStaffNameMode(storage: KeyValueStorage | null): StaffNameMode {
  return parseStaffNameMode(safeStorageGet(storage, STORAGE_KEYS.staffNameMode))
}

export function writeStaffNameMode(storage: KeyValueStorage | null, mode: StaffNameMode): void {
  safeStorageSet(storage, STORAGE_KEYS.staffNameMode, mode)
}
