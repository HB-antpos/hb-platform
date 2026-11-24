import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const usersPageSource = readFileSync('src/pages/System/Users/index.tsx', 'utf8')

const requireSource = (snippet: string, message: string) => {
  assert.equal(usersPageSource.includes(snippet), true, message)
}

requireSource('getUserAccessPermissions', '编辑用户应使用按操作者范围裁剪的权限目录接口')
requireSource("key: 'permissions'", '编辑抽屉应保留功能权限标签')
requireSource("t('system.users.functionPermissions', '功能权限')", '现有权限标签应明确命名为功能权限')
requireSource("key: 'mobile-menu'", '编辑抽屉应提供移动端菜单标签')
requireSource('UserMobileMenuPermissionManager', '移动端菜单应由独立管理组件承载')
requireSource('<Segmented', '功能权限应提供 Web 端与 POS 端分段切换')
requireSource("width=\"min(860px, 100vw)\"", '编辑抽屉宽度应适配窄视口')
requireSource('editUserRequestGuardRef', '快速切换用户时应阻止旧用户详情继续启动权限请求')
requireSource('roleRequestGuardRef', '角色草稿加载应有独立的 latest-request 守卫')
requireSource('storeRequestGuardRef', '分店草稿加载应有独立的 latest-request 守卫')
requireSource('permissionRequestGuardRef.current.invalidate()', '关闭或切换用户时应使旧权限请求失效')
requireSource('permissionSaveGuardRef', '权限保存流程应有独立的编辑会话守卫')
requireSource('permissionSaveGuardRef.current.invalidate()', '切换用户或关闭抽屉时应使旧保存回读失效')
requireSource('editSessionGuardRef', '编辑抽屉应使用独立会话标识，隔离关闭后重开同一用户的旧请求')
requireSource('editSessionGuardRef.current.invalidate()', '关闭抽屉时应使同一用户的旧编辑会话失效')
requireSource('const targetUserGuid = editingUser.userGUID', '保存应捕获发起时的目标用户，禁止后续闭包串用户')
requireSource('permissionSaveGuardRef.current.isLatest(saveRequestId)', '保存后的每个异步阶段都应校验编辑会话')
requireSource('permissionRefreshFailed', '保存成功后的刷新失败必须与写入失败区分提示')
requireSource('if (canEditUserPermissions)', '无委派资格时不应调用必然拒绝的访问权限接口')
requireSource(
  'visiblePermissionCodes.filter((permissionCode) => effectivePermSet.has(permissionCode))',
  '权限树的 checkedKeys 必须裁剪到当前 Web/POS 分类，避免向 AntD Tree 传入不存在的节点',
)
requireSource(
  'if (!canEditCurrentPermissionState || permSaving) return',
  '权限保存期间 onCheck 必须 fail-closed，禁止回读覆盖新草稿',
)
requireSource(
  'disabled={!canEditCurrentPermissionState || permSaving}',
  '权限保存期间 Tree 必须禁用，禁止继续修改草稿',
)

const saveCallIndex = usersPageSource.indexOf('await assignPermissionsToUser(targetUserGuid')
const savedBaselineIndex = usersPageSource.indexOf('setOriginalDirectPermKeys(permissions)', saveCallIndex)
assert.notEqual(saveCallIndex, -1, '权限保存应使用捕获的目标用户 GUID')
assert.equal(
  savedBaselineIndex > saveCallIndex,
  true,
  '只有权限写入成功后才能更新已保存基线；写入失败必须保留当前草稿',
)

const getHandlerSource = (handlerName: string, nextHandlerName: string) => {
  const start = usersPageSource.indexOf(`const ${handlerName} = async`)
  const end = usersPageSource.indexOf(`const ${nextHandlerName} = async`, start)
  assert.notEqual(start, -1, `应能定位 ${handlerName}`)
  assert.notEqual(end, -1, `应能定位 ${nextHandlerName}`)
  return usersPageSource.slice(start, end)
}

for (const [handlerName, nextHandlerName, guardName] of [
  ['loadRoleData', 'loadStoreData', 'roleRequestGuardRef'],
  ['loadStoreData', 'loadPermData', 'storeRequestGuardRef'],
] as const) {
  const handlerSource = getHandlerSource(handlerName, nextHandlerName)
  assert.equal(
    handlerSource.includes('const editSessionId = currentEditSessionIdRef.current'),
    true,
    `${handlerName} 应捕获发起读取时的编辑会话`,
  )
  assert.equal(
    handlerSource.includes('isCurrentEditingSession(userGuid, editSessionId)'),
    true,
    `${handlerName} 的异步结果不得写入后来打开的同一或不同用户抽屉`,
  )
  assert.equal(
    handlerSource.includes(`runLatestGuardedRequest(\n      ${guardName}.current`),
    true,
    `${handlerName} 应只允许最新请求更新草稿、错误和 loading`,
  )
  for (const status of ['loading', 'ready', 'error']) {
    assert.equal(
      handlerSource.includes(`LoadStatus('${status}')`),
      true,
      `${handlerName} 应显式进入 ${status} 状态`,
    )
  }
}

const handleEditSource = getHandlerSource('handleEdit', 'handleEditSubmit')
for (const snippet of [
  'roleRequestGuardRef.current.invalidate()',
  'storeRequestGuardRef.current.invalidate()',
  'setAllRoles([])',
  'setRoleTargetKeys([])',
  "setRoleLoadStatus('idle')",
  'setAllStores([])',
  'setStoreTargetKeys([])',
  'setStoreManageableKeys([])',
  "setStoreLoadStatus('idle')",
]) {
  assert.equal(
    handleEditSource.includes(snippet),
    true,
    `切换编辑用户时必须失效旧角色/分店请求并清空草稿：${snippet}`,
  )
}

const editDrawerStart = usersPageSource.indexOf('title={editingUser ?')
const editDrawerEnd = usersPageSource.indexOf('destroyOnHidden', editDrawerStart)
const editDrawerSource = usersPageSource.slice(editDrawerStart, editDrawerEnd)
assert.notEqual(editDrawerStart, -1, '应能定位编辑用户抽屉')
for (const snippet of [
  'roleRequestGuardRef.current.invalidate()',
  'storeRequestGuardRef.current.invalidate()',
  'setAllRoles([])',
  'setRoleTargetKeys([])',
  "setRoleLoadStatus('idle')",
  'setAllStores([])',
  'setStoreTargetKeys([])',
  'setStoreManageableKeys([])',
  "setStoreLoadStatus('idle')",
]) {
  assert.equal(
    editDrawerSource.includes(snippet),
    true,
    `关闭编辑抽屉时必须失效旧角色/分店请求并清空草稿：${snippet}`,
  )
}

for (const [handlerName, nextHandlerName] of [
  ['handleEditSubmit', 'handleSaveRoles'],
  ['handleSaveRoles', 'handleSavePermissions'],
  ['handleSaveStores', 'handleResetPassword'],
] as const) {
  const handlerSource = getHandlerSource(handlerName, nextHandlerName)
  assert.equal(
    handlerSource.includes('const targetUserGuid = editingUser.userGUID'),
    true,
    `${handlerName} 应捕获发起保存时的目标用户`,
  )
  assert.equal(
    handlerSource.includes('const editSessionId = currentEditSessionIdRef.current'),
    true,
    `${handlerName} 应捕获发起保存时的编辑会话`,
  )
  assert.equal(
    handlerSource.includes('isCurrentEditingSession(targetUserGuid, editSessionId)'),
    true,
    `${handlerName} 的异步回读不得写入后来打开的同一或不同用户抽屉`,
  )
}

for (const [handlerName, nextHandlerName, statusName, assignCall] of [
  ['handleSaveRoles', 'handleSavePermissions', 'roleLoadStatus', 'assignRolesToUser'],
  ['handleSaveStores', 'handleResetPassword', 'storeLoadStatus', 'assignStoresToUser'],
] as const) {
  const handlerSource = getHandlerSource(handlerName, nextHandlerName)
  const readyGuardIndex = handlerSource.indexOf(
    `canMutateLoadedAssignment(${statusName},`,
  )
  const assignCallIndex = handlerSource.indexOf(assignCall)
  assert.notEqual(readyGuardIndex, -1, `${handlerName} 应在未 ready 或保存中 fail-closed`)
  assert.equal(
    readyGuardIndex < assignCallIndex,
    true,
    `${handlerName} 必须在任何写接口调用前检查 ready 状态`,
  )
}

for (const [handlerName, nextHandlerName, assignCall, successKey, failedKey] of [
  ['handleSaveRoles', 'handleSavePermissions', 'assignRolesToUser', 'roleAssignSuccess', 'roleAssignFailed'],
  ['handleSaveStores', 'handleResetPassword', 'assignStoresToUser', 'storeAssignSuccess', 'storeAssignFailed'],
] as const) {
  const handlerSource = getHandlerSource(handlerName, nextHandlerName)
  const assignCallMatches = handlerSource.match(new RegExp(`await ${assignCall}\\(`, 'g')) ?? []
  const assignCallIndex = handlerSource.indexOf(`await ${assignCall}`)
  const assignFailureIndex = handlerSource.indexOf(failedKey, assignCallIndex)
  const writeFailureReturnIndex = handlerSource.indexOf('return', assignFailureIndex)
  const successIndex = handlerSource.indexOf(successKey, assignCallIndex)
  const readbackIndex = handlerSource.indexOf('await getUserByGuid(targetUserGuid)', successIndex)
  const refreshWarningIndex = handlerSource.indexOf('message.warning(', readbackIndex)
  const refreshWarningKeyIndex = handlerSource.indexOf('permissionRefreshFailed', refreshWarningIndex)

  assert.equal(assignCallMatches.length, 1, `${handlerName} 的单次保存流程只能调用一次写接口`)
  assert.equal(
    assignCallIndex < assignFailureIndex &&
      assignFailureIndex < writeFailureReturnIndex &&
      writeFailureReturnIndex < successIndex,
    true,
    `${handlerName} 只有写接口失败才能报保存失败，并且必须在成功提示前 return`,
  )
  assert.equal(
    successIndex < readbackIndex &&
      readbackIndex < refreshWarningIndex &&
      refreshWarningIndex < refreshWarningKeyIndex,
    true,
    `${handlerName} 写成功后回读失败只能提示已保存但刷新失败`,
  )
}

for (const snippet of [
  "roleLoadStatus === 'error'",
  "storeLoadStatus === 'error'",
  'onClick={retryRoleData}',
  'onClick={retryStoreData}',
  'disabled={!canMutateLoadedAssignment(roleLoadStatus, roleSaving)}',
  'disabled={!canMutateLoadedAssignment(storeLoadStatus, storeSaving)}',
]) {
  assert.equal(
    usersPageSource.includes(snippet),
    true,
    `角色/分店错误态、重试和 UI 禁用契约缺失：${snippet}`,
  )
}

assert.equal(
  usersPageSource.includes('getPermissions(),\n        getUserPermissionState(userGuid)'),
  false,
  '编辑抽屉不得继续拼接全量权限目录和未裁剪用户状态',
)

const createModalStart = usersPageSource.indexOf("title={t('system.users.createUser', '创建用户')}")
const createModalEnd = usersPageSource.indexOf('</Modal>', createModalStart)
const createModalSource = usersPageSource.slice(createModalStart, createModalEnd)
assert.notEqual(createModalStart, -1, '应能定位现有添加用户弹窗')
assert.equal(
  createModalSource.includes("key: 'permissions'") || createModalSource.includes("key: 'mobile-menu'"),
  false,
  '本次改动不得把功能权限或移动端菜单加入添加用户弹窗',
)

requireSource('buildStoreOverrideDenials', '功能权限页应识别账号级有效、但被分店收银权限覆盖关闭的 POS 权限')
requireSource("t('system.users.storeOverrideBlockedTag', '分店已关闭')", '被分店覆盖关闭的权限应在权限树里显示「分店已关闭」标签')
requireSource('getUserStorePosTerminalPermissions(editingUserGuid, store.storeGUID)', '覆盖提示应按该用户的分店逐店读取覆盖快照')
requireSource('Promise.allSettled', '逐店读取覆盖快照时单店失败（如店长越权分店）不能影响其余分店和权限编辑')
