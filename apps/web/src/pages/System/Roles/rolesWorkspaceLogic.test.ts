import assert from 'node:assert/strict'
import type { PermissionCategoryDto, RoleDto } from '../../../types/role'
import type { WebMenuPreviewNode } from '../../../utils/webMenuPreview'
import {
  buildPermissionGroupViews,
  collectWebMenuVisibility,
  diffPermissionCodes,
  expandRolePermissionCodes,
  filterRoles,
  getRoleMutationErrorMessage,
  isDerivedStoreManagerRole,
} from './rolesWorkspaceLogic'

const aliases = [
  { canonicalCode: 'Warehouse.Picking', aliasCodes: ['Warehouse.Manage', 'Warehouse.ManageOrders'] },
  { canonicalCode: 'Users.CreateStoreStaff', aliasCodes: ['Users.Create'] },
]

assert.deepEqual(
  expandRolePermissionCodes(['warehouse.manageorders', 'Stores.View'], aliases),
  ['warehouse.manageorders', 'Stores.View', 'Warehouse.Picking'],
  '持有任一别名源权限即获得目标权限，且大小写不敏感',
)
assert.deepEqual(
  expandRolePermissionCodes(['Warehouse.Picking'], aliases),
  ['Warehouse.Picking'],
  '别名是单向的：只持有目标权限不能反向获得源权限',
)
assert.deepEqual(
  expandRolePermissionCodes(['Users.Create', 'Users.CreateStoreStaff', ' '], aliases),
  ['Users.Create', 'Users.CreateStoreStaff'],
  '已显式持有的目标权限不重复，空白权限码忽略',
)

assert.deepEqual(
  diffPermissionCodes(['Stores.View', 'Stores.Sync'], ['Stores.View', 'Roles.View']),
  { added: ['Roles.View'], removed: ['Stores.Sync'] },
)

const role = (roleName: string, description?: string): RoleDto => ({
  roleGUID: roleName,
  roleName,
  description,
  isActive: true,
  createdAt: '',
  updatedAt: '',
  userCount: 0,
})
const roles = [role('StoreManager', '店长'), role('StoreStaff', '分店员工'), role('Order', '订货权限')]
assert.deepEqual(filterRoles(roles, '').map((item) => item.roleName), ['StoreManager', 'StoreStaff', 'Order'])
assert.deepEqual(filterRoles(roles, 'store').map((item) => item.roleName), ['StoreManager', 'StoreStaff'], '按名称不区分大小写过滤')
assert.deepEqual(filterRoles(roles, '订货').map((item) => item.roleName), ['Order'], '按描述过滤')

const permission = (name: string, displayName: string, category: string) => ({
  name,
  displayName,
  category,
  isSystemPermission: true,
  createdAt: '',
})
const categories: PermissionCategoryDto[] = [
  {
    category: 'Stores',
    displayName: '分店管理',
    permissions: [
      permission('Stores.View', '查看分店', 'Stores'),
      permission('Stores.Sync', '同步分店', 'Stores'),
      permission('Stores.Edit', '编辑分店', 'Stores'),
    ],
  },
  {
    category: 'Roles',
    displayName: '角色管理',
    permissions: [permission('Roles.View', '查看角色', 'Roles')],
  },
]
const baselineCodes = ['Stores.View', 'Stores.Sync']
const draftCodes = ['Stores.View', 'Roles.View']

const all = buildPermissionGroupViews({ categories, draftCodes, baselineCodes, keyword: '', filter: 'all' })
assert.deepEqual(all.counts, { all: 4, granted: 2, ungranted: 2, changed: 2 })
assert.equal(all.groups.length, 2)
const stores = all.groups.find((group) => group.key === 'Stores')
assert.ok(stores)
assert.equal(stores.total, 3)
assert.equal(stores.granted, 1, '分组统计按草稿计算')
assert.deepEqual(
  stores.items.map((item) => [item.code, item.granted, item.change]),
  [
    ['Stores.View', true, null],
    ['Stores.Sync', false, 'removed'],
    ['Stores.Edit', false, null],
  ],
)

const changed = buildPermissionGroupViews({ categories, draftCodes, baselineCodes, keyword: '', filter: 'changed' })
assert.deepEqual(
  changed.groups.flatMap((group) => group.items.map((item) => `${item.code}:${item.change}`)),
  ['Stores.Sync:removed', 'Roles.View:added'],
  '「已修改」只显示与已保存状态不同的权限',
)

const searched = buildPermissionGroupViews({ categories, draftCodes, baselineCodes, keyword: 'sync', filter: 'all' })
assert.deepEqual(searched.counts, { all: 1, granted: 0, ungranted: 1, changed: 1 }, '分段数量按搜索结果统计')
assert.equal(searched.groups.length, 1, '无匹配条目的分组不显示')
assert.equal(searched.groups[0].total, 3, '分组总数不受搜索影响')
assert.deepEqual(
  buildPermissionGroupViews({ categories, draftCodes, baselineCodes, keyword: '查看角色', filter: 'granted' })
    .groups.map((group) => group.key),
  ['Roles'],
  '可按中文名称搜索',
)

const node = (key: string, visible: boolean, children?: WebMenuPreviewNode[]): WebMenuPreviewNode => ({
  key,
  title: key,
  path: `/${key}`,
  permissionCodes: [],
  visible,
  edit: {
    canAdd: false,
    canRemove: false,
    isReadOnly: false,
    isFixed: false,
    addPermissionCodes: [],
    removePermissionCodes: [],
  },
  children,
})
assert.deepEqual(
  [...collectWebMenuVisibility([node('system', true, [node('users', false), node('roles', true)]), node('home', true)])],
  [['system', true], ['users', false], ['roles', true], ['home', true]],
)

// 店长是派生角色：与后端 Permissions.StoreManagerRoleNames 对齐，大小写不敏感。
assert.equal(isDerivedStoreManagerRole('StoreManager'), true)
assert.equal(isDerivedStoreManagerRole(' storemanager '), true)
assert.equal(isDerivedStoreManagerRole('店长'), true)
assert.equal(isDerivedStoreManagerRole('经理'), true)
assert.equal(isDerivedStoreManagerRole('WarehouseManager'), false, '仓库经理不是派生角色')
assert.equal(isDerivedStoreManagerRole(undefined), false)

// 失败提示优先展示服务端业务消息，缺失时退回通用文案。
assert.equal(
  getRoleMutationErrorMessage({ payload: { message: '店长角色由可管理分店关系维护' } }, '移除用户失败'),
  '店长角色由可管理分店关系维护',
)
assert.equal(getRoleMutationErrorMessage({ payload: { message: ' ' } }, '移除用户失败'), '移除用户失败')
assert.equal(getRoleMutationErrorMessage(new Error('Network Error'), '移除用户失败'), '移除用户失败')
