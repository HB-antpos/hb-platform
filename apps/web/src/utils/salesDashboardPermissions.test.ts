import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import type { AccessControl, CurrentUser } from '../types/auth'
import { buildAccess } from './access'
import { applyRolePermissionMutation } from './roleMenuPreview'
import { buildWebRoleMenuPreview } from './webMenuPreview'
import { getDefaultWebPath, resolveAuthorizedWebTarget } from './webPortalAccess'

// 菜单项维度：独立页面一个权限对应一个入口；进货销量分析一个入口承载两个标签权限。
interface MenuEntry { path: string; key: keyof AccessControl; codes: string[]; legacy: [string, keyof AccessControl, string][] }
const tabs: [string, keyof AccessControl, string][] = [
  ['batch-product-sales-analysis', 'canViewBatchProductSalesAnalysis', 'SalesDashboard.BatchProductSales.View'],
  ['local-supplier-purchase-sales-analysis', 'canViewLocalSupplierPurchaseSalesAnalysis', 'SalesDashboard.LocalSupplierPurchaseSales.View'],
]
const single = (path: string, key: keyof AccessControl, code: string): MenuEntry => ({ path, key, codes: [code], legacy: [] })
const entries: MenuEntry[] = [
  single('overview', 'canViewSalesData', 'SalesDashboard.SalesData.View'),
  single('sales-detail-v2', 'canViewSalesDetail', 'SalesDashboard.SalesDetail.View'),
  single('compact-sales-board', 'canViewCompactSalesBoard', 'SalesDashboard.CompactBoard.View'),
  single('monthly-daily-sales-download', 'canViewMonthlyDailySalesDownload', 'SalesDashboard.MonthlyDailySalesDownload.View'),
  single('product-movement-report', 'canViewProductMovementReport', 'SalesDashboard.ProductMovement.View'),
  { path: 'purchase-sales-analysis', key: 'canViewPurchaseSalesAnalysis', codes: tabs.map(([, , code]) => code), legacy: tabs },
  single('warehouse-product-flow-analysis', 'canViewWarehouseProductFlowAnalysis', 'SalesDashboard.WarehouseFlow.View'),
  single('local-product-sales-analysis', 'canViewLocalProductSalesAnalysis', 'SalesDashboard.LocalProductAnalysis.View'),
  single('purchase-amount-dashboard', 'canViewPurchaseAmountDashboard', 'SalesDashboard.PurchaseAmount.View'),
]
// 权限维度：每个权限码对应自己的访问键和所属菜单入口。
const grants = entries.flatMap(entry => entry.legacy.length
  ? entry.legacy.map(([legacyPath, key, code]) => ({ entry, key, code, legacyPath }))
  : [{ entry, key: entry.key, code: entry.codes[0], legacyPath: undefined as string | undefined }])
const allKeys = [...entries.map(entry => entry.key), ...tabs.map(([, key]) => key)]
const allPaths = [...entries.map(entry => entry.path), ...tabs.map(([path]) => path)]
const entryOfPath = (path: string) => entries.find(entry => entry.path === path || entry.legacy.some(([legacy]) => legacy === path))!
const prefix = '/executive-sales-intelligence'
const user = (permissions: string[], roleNames: string[] = []): CurrentUser => ({
  userGUID: 'sales-page-permissions-user', username: 'test', email: '', storeNames: [],
  permissions, exactPermissions: permissions, roleNames,
})
const preview = (access: AccessControl, permissions: string[] = []) => buildWebRoleMenuPreview(
  access, key => key, { includeHidden: true, explicitPermissionCodes: permissions },
).find(node => node.path === prefix)

for (const role of [[], ['WarehouseManager'], ['WarehouseStaff']]) {
  for (const { entry, key, code } of grants) {
    const access = buildAccess(user([code], role))
    assert.equal(access[key], true, `${role}/${code} 应放开对应页面`)
    assert.equal(access[entry.key], true, `${code} 应点亮所属菜单入口`)
    assert.equal(access.canViewSalesIntelligence, true, '任一页面权限应显示父菜单')
    assert.equal(access.canAccessAdminShell, true, '单页授权应允许进入后台')
    assert.equal(getDefaultWebPath(access), `${prefix}/${entry.path}`, '单页授权应直接进入所属入口')
    const nodes = preview(access, [code])?.children ?? []
    assert.deepEqual(nodes.filter(node => node.visible).map(node => node.path), [`${prefix}/${entry.path}`], '只应显示一个已授权入口')
    for (const otherKey of allKeys) {
      if (otherKey === key || otherKey === entry.key) continue
      assert.equal(access[otherKey], false, `${code} 不能放行 ${String(otherKey)}`)
    }
    for (const otherPath of allPaths) {
      assert.equal(resolveAuthorizedWebTarget(`${prefix}/${otherPath}`, access),
        entryOfPath(otherPath) === entry ? `${prefix}/${otherPath}` : undefined, '历史地址也应执行所属入口权限')
    }
    assert.equal(resolveAuthorizedWebTarget('/pos-admin/local-supplier-invoices', access), undefined,
      '查看看板不应同时授予进货单业务权限')
    const node = nodes.find(item => item.path === `${prefix}/${entry.path}`)!
    assert.deepEqual(node.permissionCodes, entry.codes)
    assert.deepEqual(node.edit.removePermissionCodes, entry.codes, '移除入口撤销该入口承载的全部页面权限')
  }
}

for (const permissions of [[], ['Reports.View'], ['Reports.ProductMovement.View'], ['LocalPurchase.View'], ['LocalInvocie.View']]) {
  const access = buildAccess(user(permissions))
  assert.equal(access.canViewSalesIntelligence, false, '旧业务权限不能隐式授予销售看板页面')
  for (const key of allKeys) assert.equal(access[key], false)
}

for (const role of ['Admin', '管理员', 'SuperAdmin', '超级管理员']) {
  const access = buildAccess(user([], [role]))
  assert.equal(preview(access)?.children?.filter(node => node.visible).length, entries.length, '管理员默认显示全部销售入口')
  for (const key of allKeys) assert.equal(access[key], true)
  for (const path of allPaths) assert.equal(resolveAuthorizedWebTarget(`${prefix}/${path}`, access), `${prefix}/${path}`)
}

const allCodes = entries.flatMap(entry => entry.codes)
const allNodes = preview(buildAccess(user(allCodes)), allCodes)!.children!
for (const entry of entries) {
  const node = allNodes.find(item => item.path === `${prefix}/${entry.path}`)!
  const remaining = applyRolePermissionMutation({ currentPermissionCodes: allCodes,
    removePermissionCodes: node.edit.removePermissionCodes })
  const access = buildAccess(user(remaining))
  assert.equal(access[entry.key], false, '撤销一个入口后该入口立即不满足前端权限')
  for (const [, key] of entry.legacy) assert.equal(access[key], false, '撤销合并入口后两个标签都应关闭')
  assert.equal(preview(access)?.children?.filter(item => item.visible).length, entries.length - 1, '其余销售入口应保持可见')
  const hidden = preview(buildAccess(user([])))!.children!.find(item => item.path === `${prefix}/${entry.path}`)!
  assert.deepEqual(hidden.edit.addPermissionCodes, [entry.codes[0]], '新增看板入口只授予首个页面权限，不额外授予工作台')
}

const routes = readFileSync('src/router/routes.tsx', 'utf8')
for (const entry of entries) {
  const routePath = entry.path === 'purchase-sales-analysis' ? 'ADMIN_PURCHASE_SALES_PATH' : `'${prefix}/${entry.path}'`
  const start = routes.indexOf(`path: ${routePath}`)
  const next = routes.indexOf('element:', start)
  assert.ok(start >= 0 && routes.slice(start, next).includes(`accessKey: '${entry.key}'`),
    `${entry.path} 的实际路由必须使用所属入口权限`)
}
for (const [legacyPath] of tabs) {
  const start = routes.indexOf(`path: '${prefix}/${legacyPath}'`)
  const end = routes.indexOf('/>', start)
  assert.ok(start >= 0 && routes.slice(start, end).includes('buildAdminPurchaseSalesTabPath('), `${legacyPath} 旧地址必须重定向到进货销量分析对应标签`)
}
const aliasAccess = buildAccess(user(['SalesDashboard.WarehouseFlow.View']))
assert.equal(resolveAuthorizedWebTarget(`${prefix}/product-sales-analysis`, aliasAccess), `${prefix}/product-sales-analysis`)
assert.equal(buildAccess(null).canViewSalesIntelligence, false)
console.log('销售看板逐页独立授权、合并入口、菜单、跳转、撤销及管理员默认权限测试通过')
