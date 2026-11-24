import type {
  PosTerminalPermissionOptionDto,
  UserPermissionInheritedSourceDto,
  UserPermissionStateDto,
} from '../../../types/user'
import type { PermissionCategoryDto } from '../../../types/role'

const POS_TERMINAL_PREFIX = 'Permissions.PosTerminal.'
const POS_MODULE_ORDER = ['Sales', 'Payment', 'Returns', 'SpecialProducts', 'Admin', 'Device']
const POS_MODULE_FALLBACK_NAMES: Record<string, string> = {
  Sales: 'POS 销售',
  Payment: 'POS 收款',
  Returns: 'POS 退货',
  SpecialProducts: 'POS 特殊商品',
  Admin: 'POS 管理',
  Device: 'POS 设备',
}

const LINE_DISCOUNT_PERMISSION_CODES = [
  'Permissions.PosTerminal.Sales.LineManualDiscount',
  'Permissions.PosTerminal.Sales.LineQuickDiscount10Percent',
  'Permissions.PosTerminal.Sales.LineQuickDiscount20Percent',
  'Permissions.PosTerminal.Sales.LineQuickDiscount30Percent',
  'Permissions.PosTerminal.Sales.LineQuickDiscount40Percent',
  'Permissions.PosTerminal.Sales.LineQuickDiscount50Percent',
]

const ORDER_DISCOUNT_PERMISSION_CODES = [
  'Permissions.PosTerminal.Sales.OrderManualDiscount',
  'Permissions.PosTerminal.Sales.OrderQuickDiscount10Percent',
  'Permissions.PosTerminal.Sales.OrderQuickDiscount20Percent',
  'Permissions.PosTerminal.Sales.OrderQuickDiscount30Percent',
  'Permissions.PosTerminal.Sales.OrderQuickDiscount40Percent',
  'Permissions.PosTerminal.Sales.OrderQuickDiscount50Percent',
]

const LINE_DISCOUNT_ORDER = new Map(LINE_DISCOUNT_PERMISSION_CODES.map((code, index) => [code, index]))
const ORDER_DISCOUNT_ORDER = new Map(ORDER_DISCOUNT_PERMISSION_CODES.map((code, index) => [code, index]))

export interface PosPermissionGroup {
  key: string
  displayName: string
  permissions: PosTerminalPermissionOptionDto[]
}

export interface PosPermissionSection {
  module: string
  displayName: string
  groups: PosPermissionGroup[]
}

export interface PosPermissionRequestTarget {
  sequence: number
  userGuid: string
  storeGuid: string
}

export interface PlatformPermissionCategories {
  web: PermissionCategoryDto[]
  pos: PermissionCategoryDto[]
}

export type AssignmentLoadStatus = 'idle' | 'loading' | 'ready' | 'error'

export function canMutateLoadedAssignment(
  status: AssignmentLoadStatus,
  saving: boolean,
): boolean {
  return status === 'ready' && !saving
}

export function splitPermissionCategoriesByPlatform(
  categories: PermissionCategoryDto[],
): PlatformPermissionCategories {
  const selectCategories = (isPosPermission: boolean) => categories
    .map((category) => ({
      ...category,
      permissions: category.permissions.filter((permission) =>
        permission.name.startsWith(POS_TERMINAL_PREFIX) === isPosPermission,
      ),
    }))
    .filter((category) => category.permissions.length > 0)

  return {
    web: selectCategories(false),
    pos: selectCategories(true),
  }
}

function getPosPermissionModule(permissionCode: string) {
  if (!permissionCode.startsWith(POS_TERMINAL_PREFIX)) return 'Other'
  return permissionCode.slice(POS_TERMINAL_PREFIX.length).split('.')[0] || 'Other'
}

function sortPermissionsByDisplayName(permissions: PosTerminalPermissionOptionDto[]) {
  return [...permissions].sort((left, right) =>
    left.name.localeCompare(right.name, 'zh-CN', { numeric: true }),
  )
}

function sortDiscountPermissions(
  permissions: PosTerminalPermissionOptionDto[],
  order: Map<string, number>,
) {
  return [...permissions].sort(
    (left, right) => (order.get(left.code) ?? Number.MAX_SAFE_INTEGER) - (order.get(right.code) ?? Number.MAX_SAFE_INTEGER),
  )
}

export function buildPosPermissionSections(
  assignablePermissions: PosTerminalPermissionOptionDto[],
): PosPermissionSection[] {
  const modules = new Map<string, PosTerminalPermissionOptionDto[]>()

  assignablePermissions.forEach((permission) => {
    const module = getPosPermissionModule(permission.code)
    modules.set(module, [...(modules.get(module) ?? []), permission])
  })

  // API 返回的 assignablePermissions 是唯一白名单；这里仅负责分组和固定折扣展示顺序。
  return Array.from(modules.entries())
    .sort(([left], [right]) => {
      const leftIndex = POS_MODULE_ORDER.indexOf(left)
      const rightIndex = POS_MODULE_ORDER.indexOf(right)
      return (leftIndex < 0 ? Number.MAX_SAFE_INTEGER : leftIndex) -
        (rightIndex < 0 ? Number.MAX_SAFE_INTEGER : rightIndex) || left.localeCompare(right)
    })
    .map(([module, permissions]) => {
      const lineDiscounts = permissions.filter((permission) => LINE_DISCOUNT_ORDER.has(permission.code))
      const orderDiscounts = permissions.filter((permission) => ORDER_DISCOUNT_ORDER.has(permission.code))
      const discounts = new Set([...lineDiscounts, ...orderDiscounts].map((permission) => permission.code))
      const regularPermissions = permissions.filter((permission) => !discounts.has(permission.code))
      const groups: PosPermissionGroup[] = []

      if (regularPermissions.length) {
        groups.push({
          key: `${module}:regular`,
          displayName: module === 'Sales' ? '销售操作' : '模块权限',
          permissions: sortPermissionsByDisplayName(regularPermissions),
        })
      }
      if (lineDiscounts.length) {
        groups.push({
          key: `${module}:line-discounts`,
          displayName: '单行折扣',
          permissions: sortDiscountPermissions(lineDiscounts, LINE_DISCOUNT_ORDER),
        })
      }
      if (orderDiscounts.length) {
        groups.push({
          key: `${module}:order-discounts`,
          displayName: '整单折扣',
          permissions: sortDiscountPermissions(orderDiscounts, ORDER_DISCOUNT_ORDER),
        })
      }

      return {
        module,
        displayName: permissions[0]?.group || POS_MODULE_FALLBACK_NAMES[module] || module,
        groups,
      }
    })
}

export function getEditablePosPermissionCodes(
  permissionCodes: string[],
  assignablePermissions: PosTerminalPermissionOptionDto[],
) {
  const assignableCodes = new Set(assignablePermissions.map((permission) => permission.code))
  return uniquePermissionCodes(permissionCodes.filter((permissionCode) => assignableCodes.has(permissionCode)))
}

export function buildGrantedPosPermissionCodes(
  selectedPermissionCodes: string[],
  assignablePermissions: PosTerminalPermissionOptionDto[],
) {
  return getEditablePosPermissionCodes(selectedPermissionCodes, assignablePermissions)
}

export function setPosPermissionGroupSelection(
  selectedPermissionCodes: string[],
  groupPermissionCodes: string[],
  checked: boolean,
): string[] {
  const next = new Set(selectedPermissionCodes)

  // 分类批量操作只改动当前分类，避免覆盖其他分类已经完成的草稿选择。
  groupPermissionCodes.forEach((permissionCode) => {
    if (checked) next.add(permissionCode)
    else next.delete(permissionCode)
  })

  return Array.from(next)
}

export function getPosPermissionGroupSelectionState(
  selectedPermissionCodes: string[],
  groupPermissionCodes: string[],
) {
  const selectedPermissionSet = new Set(selectedPermissionCodes)
  const selectedCount = groupPermissionCodes.filter((permissionCode) =>
    selectedPermissionSet.has(permissionCode),
  ).length
  const checked = groupPermissionCodes.length > 0 && selectedCount === groupPermissionCodes.length

  return {
    checked,
    indeterminate: selectedCount > 0 && !checked,
  }
}

export function isInheritedPosPermissionMode(mode: string | undefined) {
  const normalizedMode = mode?.trim().toLowerCase()
  return normalizedMode === 'inherited' || normalizedMode === 'inherit'
}

export function shouldEnablePosPermissionSave(mode: string | undefined, hasChanges: boolean) {
  // 继承模式允许保存当前有效快照，即使没有手工改动，也能显式切换为分店覆盖。
  return isInheritedPosPermissionMode(mode) || hasChanges
}

export function isCurrentPosPermissionRequest(
  request: PosPermissionRequestTarget,
  currentRequest: PosPermissionRequestTarget | null,
) {
  return Boolean(
    currentRequest &&
    request.sequence === currentRequest.sequence &&
    request.userGuid === currentRequest.userGuid &&
    request.storeGuid === currentRequest.storeGuid,
  )
}

export function uniquePermissionCodes(permissionCodes: string[]): string[] {
  return Array.from(new Set(permissionCodes))
}

export function buildEffectivePermissionCodes(
  inheritedPermissionCodes: string[],
  directPermissionCodes: string[],
): string[] {
  return uniquePermissionCodes([...inheritedPermissionCodes, ...directPermissionCodes])
}

export function getCheckedPermissionKeys(permissionState: UserPermissionStateDto | null): string[] {
  if (!permissionState) return []
  return uniquePermissionCodes([
    ...permissionState.inheritedPermissionCodes,
    ...permissionState.directPermissionCodes,
  ])
}

export function buildFallbackUserPermissionState({
  userGuid,
  permissions,
}: {
  userGuid: string
  permissions?: string[]
}): UserPermissionStateDto {
  const effectivePermissionCodes = uniquePermissionCodes(permissions ?? [])

  return {
    userGuid,
    isSuperAdmin: false,
    implicitAllPermissions: false,
    inheritedPermissionCodes: effectivePermissionCodes,
    directPermissionCodes: [],
    effectivePermissionCodes,
    inheritedSources: [],
  }
}

export function buildDirectPermissionPayload(permissionCodes: string[]): string[] {
  return uniquePermissionCodes(permissionCodes)
}

export function toggleDirectPermission({
  currentDirectPermissions,
  inheritedPermissionCodes,
  permissionCode,
  checked,
}: {
  currentDirectPermissions: string[]
  inheritedPermissionCodes: string[]
  permissionCode: string
  checked: boolean
}): string[] {
  const next = new Set(currentDirectPermissions)

  if (checked) {
    next.add(permissionCode)
  } else {
    next.delete(permissionCode)
  }

  if (!checked && inheritedPermissionCodes.includes(permissionCode)) {
    next.delete(permissionCode)
  }

  return buildDirectPermissionPayload(Array.from(next))
}

export function deriveDirectPermissionKeysFromChecked({
  checkedPermissionKeys,
  allPermissionCodes,
  inheritedPermissionCodes,
  currentDirectPermissionCodes,
}: {
  checkedPermissionKeys: string[]
  allPermissionCodes: string[]
  inheritedPermissionCodes: string[]
  currentDirectPermissionCodes: string[]
}): string[] {
  const checkedSet = new Set(checkedPermissionKeys)
  const inheritedSet = new Set(inheritedPermissionCodes)
  const currentDirectSet = new Set(currentDirectPermissionCodes)

  return buildDirectPermissionPayload(
    allPermissionCodes.filter((permissionCode) =>
      checkedSet.has(permissionCode) &&
      (!inheritedSet.has(permissionCode) || currentDirectSet.has(permissionCode)),
    ),
  )
}

export function mergeVisibleDirectPermissionSelection({
  checkedPermissionKeys,
  visiblePermissionCodes,
  inheritedPermissionCodes,
  currentDirectPermissionCodes,
}: {
  checkedPermissionKeys: string[]
  visiblePermissionCodes: string[]
  inheritedPermissionCodes: string[]
  currentDirectPermissionCodes: string[]
}): string[] {
  const visiblePermissionCodeSet = new Set(visiblePermissionCodes)
  const nextVisibleDirectPermissions = deriveDirectPermissionKeysFromChecked({
    checkedPermissionKeys,
    allPermissionCodes: visiblePermissionCodes,
    inheritedPermissionCodes,
    currentDirectPermissionCodes,
  })

  // 当前树只包含一个终端，另一终端的未保存草稿必须原样保留。
  return buildDirectPermissionPayload([
    ...currentDirectPermissionCodes.filter(
      (permissionCode) => !visiblePermissionCodeSet.has(permissionCode),
    ),
    ...nextVisibleDirectPermissions,
  ])
}

export function buildPermissionSourceMap(
  inheritedSources: UserPermissionInheritedSourceDto[],
): Record<string, string[]> {
  const sourceMap: Record<string, string[]> = {}

  inheritedSources.forEach((source) => {
    source.permissionCodes.forEach((permissionCode) => {
      sourceMap[permissionCode] = [...(sourceMap[permissionCode] ?? []), source.roleName]
    })
  })

  return sourceMap
}

export function arePermissionSetsEqual(left: string[], right: string[]): boolean {
  const leftSet = new Set(left)
  const rightSet = new Set(right)

  if (leftSet.size !== rightSet.size) return false

  for (const item of leftSet) {
    if (!rightSet.has(item)) return false
  }

  return true
}

/** 单个分店的收银权限覆盖快照：只保留构造「被覆盖压住」提示所需的字段。 */
export interface StorePosOverrideSnapshot {
  storeGuid: string
  storeName: string
  /** 该用户在该店存在覆盖行的权限码（授权或不授权都算）。 */
  overriddenPermissionCodes: string[]
  /** 覆盖行里明确授权的权限码；覆盖了但不在此列表里 = 在该店被显式关闭。 */
  grantedPermissionCodes: string[]
}

/**
 * 找出「账号级有效、但在某些分店被分店收银权限覆盖关闭」的 POS 权限。
 *
 * 关键逻辑：POS 登录快照 = 角色权限 + 账号直授，再被分店覆盖逐项改写，覆盖里显式关闭的项会被删掉。
 * 所以账号级勾上了、某店却被关闭的权限，在那家店的 POS 上是无效的，必须提示，否则容易误以为已经生效。
 * 只统计 accountEffectiveCodes（角色继承 + 直授）里存在的 POS 码；返回 权限码 → 被关闭的分店名列表。
 */
export function buildStoreOverrideDenials(
  snapshots: StorePosOverrideSnapshot[],
  accountEffectiveCodes: Iterable<string>,
): Record<string, string[]> {
  const effectiveCodes = new Set(accountEffectiveCodes)
  const denials: Record<string, string[]> = {}

  snapshots.forEach((snapshot) => {
    const grantedCodes = new Set(snapshot.grantedPermissionCodes)
    snapshot.overriddenPermissionCodes.forEach((permissionCode) => {
      if (!permissionCode.startsWith(POS_TERMINAL_PREFIX)) return
      if (grantedCodes.has(permissionCode)) return
      if (!effectiveCodes.has(permissionCode)) return
      denials[permissionCode] = [...(denials[permissionCode] ?? []), snapshot.storeName]
    })
  })

  return denials
}
