import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// 收银端设置页接线契约：测试不能 import 页面组件 / CSS，所以读源码静态核对关键约定。

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

const directory = 'src/pages/System/PosSettings'
const read = (path: string) => readFileSync(resolve(path), 'utf8')
const page = read(`${directory}/index.tsx`)
const globalTab = read(`${directory}/GlobalStoresTab.tsx`)
const devicesTab = read(`${directory}/DevicesTab.tsx`)
const storeDrawer = read(`${directory}/StoreDrawer.tsx`)
const deviceDrawer = read(`${directory}/DeviceDrawer.tsx`)
const changesModal = read(`${directory}/ChangesModal.tsx`)
const confirmModal = read(`${directory}/ConfirmChangesModal.tsx`)
const parts = read(`${directory}/parts.tsx`)
const logic = read(`${directory}/logic.ts`)
const css = read(`${directory}/posSettings.css`)
const service = read('src/services/posSettingsService.ts')
const routes = read('src/router/routes.tsx')
const access = read('src/utils/access.ts')
const portal = read('src/utils/webPortalAccess.ts')
const preview = read('src/utils/webMenuPreview.ts')
const expoPreview = read('src/utils/expoRoleMenuPreview.ts')
const permissions = read('src/types/permissions.ts')
const authTypes = read('src/types/auth.ts')
const pkg = read('package.json')
const navigation = read('../../services/backend/BlazorApp.Api/Services/NavigationService.cs')
const pageSources = [page, globalTab, devicesTab, storeDrawer, deviceDrawer, changesModal, confirmModal, parts, logic]

// 数据：只经服务层调用约定的接口，页面不直接写路径
assert(service.includes("'/api/react/v1/pos-settings'"), '服务必须请求 api/react/v1/pos-settings')
for (const path of ['catalog', 'global', 'stores', 'devices', 'changes']) {
  assert(service.includes(`url('${path}')`) || service.includes(`url(\`${path}`), `服务缺少 ${path} 接口`)
}
for (const source of pageSources) {
  assert(!source.includes('/api/react/v1/pos-settings'), '页面必须经 posSettingsService 调用接口')
}
// 设置项清单以接口返回的 catalog 为准，页面不写死 9 个键的清单（文案映射在 parts.tsx 的 switch 里，不算清单）
assert(page.includes('getPosSettingsCatalog') && page.includes('catalogData?.definitions'), '设置项清单来自 catalog 接口')
for (const source of [page, globalTab, devicesTab, storeDrawer, deviceDrawer]) {
  assert(!/['"](?:cashMovement|cashDrawer|returns|receipt|payment|maintenance|zeller)\.[A-Za-z]+['"]/.test(source), '页面组件不能写死设置项键')
}

// 保活页面：隐藏时不取数
assert(page.includes('useKeepAliveContext') && page.includes('useRequest(\'catalog\'') && page.includes('active)'), '保活页面隐藏时不取数')
assert(page.includes('active && tab === \'devices\''), '设备页签激活时才取设备列表')
assert(storeDrawer.includes('active && target !== null') && deviceDrawer.includes('active && target !== null'), '抽屉关闭时不取数')
assert(storeDrawer.includes('requestKey') && deviceDrawer.includes('requestKey') && page.includes('seqRef.current += 1') && devicesTab.includes('seqRef.current += 1'), '每次打开抽屉都重新取数')

// 权限：只有 Manage 才能编辑；View 只读且不显示保存按钮；不按角色推断
assert(page.includes('state.access.canManagePosSettings'), '编辑权限只认 canManagePosSettings')
for (const source of pageSources) {
  assert(!/hasRole\(|isAdmin|isStoreManager/.test(source), '页面不能按角色推断（全局层管理员限制由后端 403 返回）')
}
assert(globalTab.includes('disabled={!canManage}') && storeDrawer.includes('disabled={!canManage}') && deviceDrawer.includes('disabled={!canManage}'), '没有 Manage 时三态控件只读')
assert(globalTab.includes('{canManage ? (') && storeDrawer.includes('footer={canManage ?') && deviceDrawer.includes('footer={canManage ?'), '没有 Manage 时不显示保存按钮')
assert(parts.includes('disabled={disabled}'), '三态控件透传 disabled')

// 三态：全局 / 门店 / 设备都是三态，「不覆盖」文案随层级
assert(globalTab.includes('layer="global"') && storeDrawer.includes('layer="store"') && deviceDrawer.includes('layer="device"'), '三层都用三态控件')
assert(/TRI_STATES[\s\S]*'inherit'[\s\S]*'on'[\s\S]*'off'/.test(logic), '三态为 inherit / on / off')
assert(parts.includes("tr('tri.inheritGlobal')") && parts.includes("tr('tri.inheritStore')") && parts.includes("tr('tri.inheritDevice')"), '三层的「不覆盖」文案不同')
// 保存请求只带改动的项，清除用 null
assert(logic.includes('triToValue(change.to)') && globalTab.includes('buildSaveValues(changes)') && storeDrawer.includes('buildSaveValues(changes)') && deviceDrawer.includes('buildSaveValues(changes)'), '保存只带改动的项')

// 保存前一律先确认，保存成功后刷新数据；失败信息（含 403 的后端 message）留在确认弹窗里
for (const [name, source] of [['全局', globalTab], ['门店', storeDrawer], ['设备', deviceDrawer]] as const) {
  assert(source.includes('<ConfirmChangesModal') && source.includes('onConfirm={save}'), `${name}保存必须先弹确认`)
  assert(source.includes('setConfirmOpen(true)'), `${name}保存按钮只打开确认弹窗，不直接提交`)
  assert(source.includes('message.success(tr(\'saved\'))'), `${name}保存成功要提示`)
}
assert(globalTab.includes('onChanged()') && storeDrawer.includes('detailRequest.reload()') && deviceDrawer.includes('detailRequest.reload()'), '保存成功后刷新数据')
assert(confirmModal.includes('triLabel(layer, change.from, tr)') && confirmModal.includes('triLabel(layer, change.to, tr)') && confirmModal.includes('maskClosable={false}'), '确认弹窗逐项列出旧 → 新')
assert(logic.includes('resolved.status === 403') && logic.includes('resolved.serverMessage'), '403 显示后端 message')

// 设备页：应生效 + 设备回报 + 待同步；状态标签三色
assert(deviceDrawer.includes('reportedState(') && deviceDrawer.includes("tr('deviceDrawer.pendingTag')"), '设备抽屉显示「待同步」')
assert(deviceDrawer.includes("tr('deviceDrawer.colEffective')") && deviceDrawer.includes("tr('deviceDrawer.colReported')"), '设备抽屉同时显示应生效与设备回报')
assert(logic.includes("'applied'") && logic.includes("'green'") && logic.includes("'orange'") && logic.includes("'gray'"), '设备状态三色')
assert(parts.includes("tr('deviceStatus.neverHint')"), '尚未同步要提示原因')
assert(devicesTab.includes('stores[0]?.storeCode'), '设备页签默认第一个可见门店')
assert(!read('src/pages/PosAdmin/DeviceRegistration/index.tsx').includes('posSettings'), '不改现有设备管理页')

// 页头「修改记录」
assert(page.includes('<ChangesModal') && page.includes("tr('historyButton')") && changesModal.includes('listPosSettingChanges({ limit: 50 }'), '页头修改记录按钮与弹窗')

// 页面只有两个页签
assert(page.includes("key: 'global'") && page.includes("key: 'devices'") && (page.match(/key: '/g) ?? []).length === 2, '只有「全局与门店」「设备」两个页签')

// 表格：只用 MeasuredTable，metricId 唯一
const metricIds = [globalTab, devicesTab, storeDrawer, deviceDrawer, changesModal]
  .flatMap((source) => [...source.matchAll(/metricId="([^"]+)"/g)].map((match) => match[1]))
assert(metricIds.length >= 6, '每张表都要有 metricId')
assert(new Set(metricIds).size === metricIds.length, `metricId 必须唯一：${metricIds.join(',')}`)
assert(metricIds.every((id) => id.startsWith('system.pos-settings.')), 'metricId 统一前缀 system.pos-settings.')
for (const source of [globalTab, devicesTab, storeDrawer, deviceDrawer, changesModal]) {
  assert(source.includes('<MeasuredTable'), '表格必须用 MeasuredTable')
  assert(!/import\s*\{[^}]*\bTable\b[^}]*\}\s*from\s*'antd'/.test(source), '不能直接从 antd 引入 Table')
}
// 全局契约（noAutomaticReload）会扫描 pages 下所有文件里的该字面量，所以这里拆开拼接。
for (const source of pageSources) {
  assert(!source.includes(['window', 'location', 'reload'].join('.')), '页面不得自动 hard reload')
}

// 样式：窄屏堆叠、减少动画偏好
assert(css.includes('@media (max-width: 767px)') && css.includes('@media (max-width: 1100px)') && css.includes('prefers-reduced-motion'), '必须有窄屏与减少动画处理')

// 路由与权限登记
const routeStart = routes.indexOf("path: '/system/pos-settings'")
assert(routeStart >= 0, '必须登记路由')
const routeBlock = routes.slice(routeStart, routes.indexOf('element:', routeStart))
assert(routeBlock.includes("title: 'menu.posSettings'"), '路由标题使用全局菜单键')
assert(routeBlock.includes("icon: 'ControlOutlined'"), '路由图标 ControlOutlined')
assert(routeBlock.includes('keepAlive: true'), '路由必须 keepAlive')
assert(routeBlock.includes("accessKey: 'canViewPosSettings'"), '路由使用本页访问键')
assert(routes.includes("lazy(() => import('../pages/System/PosSettings'))"), '页面必须懒加载')
assert(routes.includes('element: <PosSettingsPage />'), '路由必须渲染收银端设置页')
assert(/ControlOutlined:\s*<ControlOutlined\s*\/>/.test(routes), '后端菜单图标必须在 iconMap 里，否则侧边栏不显示图标')
assert(permissions.includes("View: 'PosSettings.View'") && permissions.includes("Manage: 'PosSettings.Manage'"), '权限码常量')
assert(authTypes.includes('canViewPosSettings: boolean') && authTypes.includes('canManagePosSettings: boolean'), 'AccessControl 新字段')
assert(access.includes('const canManagePosSettings = isAdmin || hasPermission(P.PosSettings.Manage)'), 'access 读取 Manage 权限')
assert(access.includes('const canViewPosSettings = canManagePosSettings || hasPermission(P.PosSettings.View)'), 'View 或 Manage 任一可进入')
assert(portal.includes("defaultPath: '/system/pos-settings'") && portal.includes('P.PosSettings.View') && portal.includes('P.PosSettings.Manage'), '门户落地规则必须包含本页')
assert(preview.includes("{ path: '/system/pos-settings', title: 'menu.posSettings', accessKey: 'canViewPosSettings' }"), '菜单预览必须包含本页')
assert(preview.includes('canViewPosSettings: [P.PosSettings.View, P.PosSettings.Manage]'), '菜单预览访问键映射')
assert(expoPreview.includes("routeName: 'pos-settings'") && expoPreview.includes('permissionCodes: [P.PosSettings.View]') && !expoPreview.includes("'PosSettings.View'"), 'App 菜单预览引用权限常量，不写死权限码')

// 与后端 Web 菜单一致：同一路径、同一标题键、同一图标、同一组权限
assert(
  /Path = "\/system\/pos-settings".*TitleKey = "menu\.posSettings".*Icon = "ControlOutlined".*Permissions\.PosSettings\.View.*Permissions\.PosSettings\.Manage/.test(navigation),
  '后端 FullMenu 应登记收银端设置（PosSettings.View / Manage 任一可见）',
)

// 测试脚本接进总 test
assert(pkg.includes('"test:pos-settings"') && pkg.includes('npm run test:pos-settings'), '必须有 test:pos-settings 脚本并接进总 test')

console.log('posSettings source contract: ok')
