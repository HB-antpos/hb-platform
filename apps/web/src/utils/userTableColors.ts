import { getStableTagColor } from './tagColors'

const ROLE_COLOR_MAP: Record<string, string> = {
  Admin: 'red',
  管理员: 'red',
  Manager: 'orange',
  经理: 'orange',
  WarehouseManager: 'gold',
  仓库经理: 'gold',
  StoreManager: 'green',
  店长: 'green',
  StoreStaff: 'cyan',
  店铺员工: 'cyan',
  WarehouseStaff: 'blue',
  仓库员工: 'blue',
  Order: 'purple',
  订货员: 'purple',
  User: 'default',
  用户: 'default',
}

export function getRoleColor(roleName: string): string {
  return ROLE_COLOR_MAP[roleName] || 'default'
}

// 角色色点用的实色：与 getRoleColor 的 AntD 预设色一一对应，未知角色回落为中性灰。
const ROLE_ACCENT_HEX: Record<string, string> = {
  red: '#f5222d',
  orange: '#fa8c16',
  gold: '#d99a00',
  green: '#52c41a',
  cyan: '#13c2c2',
  blue: '#1677ff',
  purple: '#722ed1',
  default: '#8c93a1',
}

export function getRoleAccentColor(roleName: string): string {
  return ROLE_ACCENT_HEX[getRoleColor(roleName)] ?? ROLE_ACCENT_HEX.default
}

export function getStoreColor(storeName: string): string {
  return getStableTagColor(storeName)
}
