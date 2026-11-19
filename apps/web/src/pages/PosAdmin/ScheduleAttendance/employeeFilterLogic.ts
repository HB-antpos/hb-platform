// 考勤记录 / 员工可用时间 / 打卡记录页签的「员工筛选下拉」与「员工列显示名」。纯函数，便于单测。
// 显示名口径与周排班看板一致（见 staffNameLogic）：昵称 → 名 → 全名 → 登录名，昵称模式下重名自动区分。

import { resolveStaffName, resolveStaffNames } from './staffNameLogic'
import type { StaffNameMode, StaffNameSource } from './staffNameLogic'

/** 员工下拉的一个选项。 */
export interface EmployeeOption {
  /** userGuid；选中后作为接口的 userGuid 查询参数。 */
  value: string
  /** 当前开关下的显示名；选中后输入框里显示的就是它。 */
  label: string
  /** 下拉右侧灰字：另一种叫法（昵称模式显示全名，全名模式显示昵称）；两者相同时退回登录名。 */
  secondary: string
  /** 搜索用的小写文本：昵称、名、姓、全名、登录名、GUID 都能搜到。 */
  search: string
}

/** 把分店员工（接口 DTO）转成显示名原料；看板与员工筛选共用，保证两处口径一致。 */
export function toStaffNameSource(user: {
  userGuid: string
  username?: string | null
  fullName?: string | null
  givenName?: string | null
  familyName?: string | null
  nickname?: string | null
}): StaffNameSource {
  return {
    userGuid: user.userGuid,
    username: user.username,
    fullName: user.fullName,
    givenName: user.givenName,
    familyName: user.familyName,
    nickname: user.nickname,
  }
}

function clean(value: string | null | undefined): string | undefined {
  const text = (value ?? '').trim()
  return text === '' ? undefined : text
}

/** GUID 前 8 位：名字取不到时代替姓名显示，完整 GUID 放悬停提示里。 */
export function shortGuid(userGuid: string): string {
  return userGuid.slice(0, 8)
}

/**
 * 员工下拉的选项，按当前显示名排序。
 * 搜索文本把昵称 / 名 / 姓 / 全名 / 登录名 / GUID 都放进去，另外拼上「昵称 姓」「名 姓」，
 * 这样输入 `ethan chen` 这种混合写法也能找到人；换了显示方式也不会搜不到。
 */
export function buildEmployeeOptions(sources: readonly StaffNameSource[], mode: StaffNameMode): EmployeeOption[] {
  const labels = resolveStaffNames(sources, mode)
  // 另一种叫法：昵称模式看全名；全名模式看昵称（含重名区分），保证两边展示的名字能互相对上
  const nicknameNames = mode === 'full' ? resolveStaffNames(sources, 'nickname') : null

  const options = sources.map<EmployeeOption>((source) => {
    const label = labels.get(source.userGuid) ?? source.userGuid
    const other = mode === 'nickname' ? resolveStaffName(source, 'full') : nicknameNames?.get(source.userGuid)
    const nickname = clean(source.nickname)
    const given = clean(source.givenName)
    const family = clean(source.familyName)
    const search = [
      nickname,
      given,
      family,
      clean(source.fullName),
      clean(source.username),
      source.userGuid,
      label,
      nickname && family ? `${nickname} ${family}` : undefined,
      given && family ? `${given} ${family}` : undefined,
    ]
      .filter((item): item is string => Boolean(item))
      .join('\n')
      .toLowerCase()
    return {
      value: source.userGuid,
      label,
      secondary: other && other !== label ? other : clean(source.username) ?? '',
      search,
    }
  })

  return options.sort((left, right) => left.label.localeCompare(right.label, undefined, { sensitivity: 'base' }))
}

/** 关键字是否命中某个选项的搜索文本；空关键字全部命中，忽略大小写和首尾空格。 */
export function matchesEmployeeKeyword(search: string | undefined, keyword: string): boolean {
  const key = keyword.trim().toLowerCase()
  if (key === '') return true
  return (search ?? '').includes(key)
}

export interface HighlightSegment {
  text: string
  match: boolean
}

/** 把文本按关键字切成「命中 / 未命中」片段，用来在下拉里把命中的字标蓝；关键字当普通文本匹配，不当正则。 */
export function splitHighlight(text: string, keyword: string): HighlightSegment[] {
  const key = keyword.trim()
  if (key === '' || text === '') return [{ text, match: false }]
  const escaped = key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  // 带捕获组的 split：奇数位就是命中的片段
  const parts = text.split(new RegExp(`(${escaped})`, 'i'))
  return parts
    .map((part, index) => ({ text: part, match: index % 2 === 1 }))
    .filter((segment) => segment.text !== '')
}

/** 员工列要显示的内容。 */
export interface EmployeeCellDisplay {
  /** 第一行：当前开关下的显示名；名字完全取不到时是 GUID 前 8 位。 */
  primary: string
  /** 第二行灰字：另一种叫法，和第一行相同则没有。 */
  alt?: { kind: 'full' | 'nickname'; name: string }
  /** 名字完全取不到（不在名单里、记录自己也没带名字）。 */
  unresolved: boolean
}

/**
 * 员工列显示：
 * - 名单里的人：昵称模式第一行昵称（没填用名兜底，重名已区分）、第二行全名；全名模式反过来。
 * - 不在名单里的人（管理员 / 仓库经理在店里打卡、已离职、没选分店、或名单还没加载）：用记录自带的
 *   昵称 → 名 → 全名（打卡接口会带回这三项；考勤记录 / 可用时间接口只带全名）；都没有才显示 GUID 前 8 位。
 */
export function resolveEmployeeCell(input: {
  userGuid: string
  mode: StaffNameMode
  /** 名单里的这个人（带昵称 / 名 / 姓）。 */
  source?: StaffNameSource
  /** 昵称模式下已做重名区分的显示名（来自 resolveStaffNames），缺省则按单人规则算。 */
  nicknameName?: string
  /** 记录自带的全名（考勤记录 / 可用时间 / 打卡接口都返回）。 */
  recordName?: string
  /** 记录自带的昵称（目前只有打卡接口返回）；只在名单里找不到这个人时使用。 */
  recordNickname?: string
  /** 记录自带的「名」（目前只有打卡接口返回）；没填昵称时兜底，只在名单里找不到这个人时使用。 */
  recordGivenName?: string
}): EmployeeCellDisplay {
  const { userGuid, mode, source } = input
  const recordName = clean(input.recordName)
  const full = clean(source?.fullName) ?? recordName ?? clean(source?.username)
  // 昵称叫法：名单里的人按名单算（昵称 → 名 → 全名，含重名区分）；
  // 不在名单里的人用记录自带的昵称 → 名，记录也没带就只能是全名
  const nickname = source
    ? clean(input.nicknameName) ?? resolveStaffName(source, 'nickname')
    : clean(input.recordNickname) ?? clean(input.recordGivenName) ?? full

  if (!full && (!nickname || nickname === userGuid)) {
    return { primary: shortGuid(userGuid), unresolved: true }
  }

  const fullText = full ?? nickname ?? shortGuid(userGuid)
  const nicknameText = nickname && nickname !== userGuid ? nickname : fullText
  const primary = mode === 'nickname' ? nicknameText : fullText
  const other = mode === 'nickname' ? fullText : nicknameText
  return {
    primary,
    alt: other !== primary ? { kind: mode === 'nickname' ? 'full' : 'nickname', name: other } : undefined,
    unresolved: false,
  }
}
