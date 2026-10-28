import type { ReactElement } from 'react'
import type { AccessControl } from './auth'

export interface AppRouteMeta {
  title: string
  icon?: string
  hidden?: boolean
  affix?: boolean
  closable?: boolean
  keepAlive?: boolean
  accessKey?: keyof AccessControl
  /** 任一权限为真即可访问（合并页菜单：任一标签有权限即显示）；与 accessKey 同时存在时两者都要满足。 */
  anyAccessKeys?: (keyof AccessControl)[]
  activeMenu?: string
  /** 所属合并页的菜单路径：同组路由共用一个顶部页签，页内用标签切换。 */
  navGroup?: string
  dynamicTitle?: (params: Record<string, string>) => string
}

/** 合并页里的一个标签：就是原来的独立页面，地址、权限不变。 */
export interface NavGroupTab {
  path: string
  title: string
  accessKey: keyof AccessControl
  /**
   * 标签内部还承载了其他页面（如排班考勤页内嵌考勤审核）时，任一权限为真即可进入该标签；
   * 设置后以它为准，accessKey 仅作为主权限的说明保留。页内各子标签再按各自权限显示。
   */
  anyAccessKeys?: (keyof AccessControl)[]
  /**
   * false：切走或离开合并页即卸载（页面内存里会短暂持有凭据、摄像机地址、照片等敏感数据）；
   * true：切走后保留状态（筛选、分页等）。
   */
  keepAlive: boolean
  element: ReactElement
}

export interface NavGroupDefinition {
  /** 侧栏菜单项地址；直接访问时跳到第一个有权限的标签。 */
  path: string
  title: string
  icon: string
  tabs: NavGroupTab[]
}

export interface AppRouteItem {
  path: string
  meta: AppRouteMeta
  element?: ReactElement
  children?: AppRouteItem[]
}

export interface TabItem {
  key: string
  path: string
  routePath: string
  title: string
  affix?: boolean
  closable?: boolean
  keepAlive?: boolean
}
