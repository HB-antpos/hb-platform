import assert from 'node:assert/strict'
import dayjs from 'dayjs'
import {
  LOGIN_TOGGLE_ERROR_MESSAGES,
  STALE_LOGIN_DAYS,
  countBatchStoreChanges,
  getLoginToggleState,
  getServerErrorMessage,
  describeLastLogin,
  diffAssignmentKeys,
  diffStoreAssignment,
  diffUserNameParts,
  getUserDisplayName,
  getUserInitial,
  getUserSecondaryLine,
  isDerivedStoreManagerRoleName,
  isLoginRestricted,
  mergeSelectedUsers,
  planBatchRoleChange,
  pickUserNamePartFormValues,
  planBatchStatusChange,
  planLoginToggleNotice,
  resolveLoginToggleErrorKind,
  splitVisibleStores,
  toIsActiveQuery,
  withLoginEnabled,
} from './usersPageLogic'
import type { UserDto } from '../../../types/user'

assert.equal(toIsActiveQuery('all'), undefined, '「全部」不应向后端传 isActive')
assert.equal(toIsActiveQuery('active'), true, '「启用」应查询 isActive=true')
assert.equal(toIsActiveQuery('inactive'), false, '「停用」应查询 isActive=false')

// 用本地时间构造「现在」，保证跨天判断在任何时区下都确定。
const now = new Date(2026, 9, 5, 12, 30, 0)
const localIso = (...parts: [number, number, number, number, number]) =>
  dayjs(new Date(parts[0], parts[1], parts[2], parts[3], parts[4])).toISOString()

assert.deepEqual(describeLastLogin(undefined, now), { kind: 'never' }, '没有登录时间应显示从未登录')
assert.deepEqual(describeLastLogin('not-a-date', now), { kind: 'never' }, '非法时间按从未登录处理')
assert.deepEqual(
  describeLastLogin(dayjs(now).subtract(30, 'second').toISOString(), now),
  { kind: 'justNow' },
  '一分钟内应显示刚刚',
)
assert.deepEqual(
  describeLastLogin(dayjs(now).add(5, 'minute').toISOString(), now),
  { kind: 'justNow' },
  '服务器时钟略快导致的未来时间不能显示为负数',
)
assert.deepEqual(
  describeLastLogin(dayjs(now).subtract(5, 'minute').toISOString(), now),
  { kind: 'minutes', count: 5 },
  '一小时内按分钟显示',
)
assert.deepEqual(
  describeLastLogin(dayjs(now).subtract(5, 'hour').subtract(27, 'minute').toISOString(), now),
  { kind: 'hours', count: 5 },
  '一天内按整小时显示',
)
assert.deepEqual(
  describeLastLogin(localIso(2026, 9, 4, 9, 0), now),
  { kind: 'yesterday' },
  '超过 24 小时且在前一个自然日应显示昨天',
)
assert.deepEqual(
  describeLastLogin(localIso(2026, 9, 2, 18, 0), now),
  { kind: 'days', count: 3, stale: false },
  '跨多天按自然日计数',
)
assert.deepEqual(
  describeLastLogin(localIso(2026, 7, 19, 10, 31), now),
  { kind: 'days', count: 47, stale: true },
  `超过 ${STALE_LOGIN_DAYS} 天未登录应标记为长期未登录`,
)
assert.deepEqual(
  describeLastLogin('2026-10-05 01:00:00', new Date('2026-10-05T01:10:00Z')),
  { kind: 'minutes', count: 10 },
  '后端缺少时区后缀的时间应按 UTC 解析',
)

assert.equal(getUserDisplayName({ username: 'tommy', fullName: 'tommy' }), 'tommy')
assert.equal(getUserDisplayName({ username: 'jl', fullName: '  ' }), 'jl', '空白姓名应回落到用户名')
assert.equal(
  getUserSecondaryLine({ username: 'tommy', fullName: 'tommy', email: 'tommy@123.com' }),
  'tommy@123.com',
  '姓名与用户名相同时第二行只显示邮箱',
)
assert.equal(
  getUserSecondaryLine({ username: 'jl', fullName: 'Jane Lee', email: 'jane@123.com' }),
  '@jl · jane@123.com',
  '姓名与用户名不同时第二行应补充用户名',
)
assert.equal(getUserSecondaryLine({ username: 'jl', fullName: 'Jane Lee' }), '@jl')

assert.equal(getUserInitial('vellyn'), 'V')
assert.equal(getUserInitial('张三'), '张', '中文姓名取首字')
assert.equal(getUserInitial('   '), '?')

assert.deepEqual(
  splitVisibleStores(['Glendale', 'Cronulla', 'Bondi Junction']),
  { visible: ['Bondi Junction', 'Cronulla'], hidden: ['Glendale'] },
  '分店按名称排序后只显示前两家',
)
assert.deepEqual(splitVisibleStores(undefined), { visible: [], hidden: [] })

assert.deepEqual(
  diffAssignmentKeys(['a', 'b'], ['b', 'c']),
  { added: ['c'], removed: ['a'] },
)

assert.deepEqual(
  diffStoreAssignment({
    baselineStores: ['s1', 's2'],
    draftStores: ['s1', 's2', 's3'],
    baselineManageable: ['s1'],
    draftManageable: ['s2', 's3'],
  }),
  { added: ['s3'], removed: [], manageableChanged: ['s1', 's2'] },
  '新增分店的可管理状态随新增保存，不重复计入可管理变更',
)

const batchUser = (userGUID: string, isActive: boolean, roleNames: string[] = []): UserDto => ({
  userGUID,
  username: userGUID,
  email: `${userGUID}@example.test`,
  isActive,
  createdAt: '2026-10-01T00:00:00Z',
  updatedAt: '2026-10-01T00:00:00Z',
  roleNames,
  storeNames: [],
})

// 跨页勾选：其他页的行对象从上一次选择补回，取消勾选的行被丢弃，顺序跟随 keys。
{
  const a = batchUser('a', true)
  const b = batchUser('b', true)
  const c = batchUser('c', false)
  const merged = mergeSelectedUsers([a, b], ['a', 'c'], [undefined, c])
  assert.deepEqual(merged.map((user) => user.userGUID), ['a', 'c'], '保留跨页的 a，加入本页的 c，去掉已取消的 b')
  const refreshed = { ...a, isActive: false }
  assert.equal(mergeSelectedUsers([a], ['a'], [refreshed])[0].isActive, false, '本页回传的新行对象覆盖旧快照')
}

// 批量启停用：跳过自己、已是目标状态、范围外账号。
{
  const users = [batchUser('me', true), batchUser('on', true), batchUser('off', false), batchUser('boss', true)]
  const plan = planBatchStatusChange(users, false, 'me', (user) => user.userGUID === 'boss')
  assert.deepEqual(plan.targets.map((user) => user.userGUID), ['on'])
  assert.deepEqual(plan.skippedSelf.map((user) => user.userGUID), ['me'])
  assert.deepEqual(plan.skippedUnchanged.map((user) => user.userGUID), ['off'])
  assert.deepEqual(plan.skippedOutOfScope.map((user) => user.userGUID), ['boss'])
  assert.deepEqual(
    planBatchStatusChange(users, true, 'me').targets.map((user) => user.userGUID),
    ['off'],
    '批量启用只提交当前停用的账号',
  )
}

// 批量角色：添加跳过已持有者，移除跳过未持有者，角色名大小写不敏感。
{
  const users = [batchUser('x', true, ['Staff']), batchUser('y', true, ['staff ', 'Admin']), batchUser('z', true, [])]
  assert.deepEqual(planBatchRoleChange(users, 'Staff', 'add').targets.map((user) => user.userGUID), ['z'])
  assert.deepEqual(planBatchRoleChange(users, 'STAFF', 'remove').targets.map((user) => user.userGUID), ['x', 'y'])
  assert.deepEqual(planBatchRoleChange(users, 'Staff', 'remove').skipped.map((user) => user.userGUID), ['z'])
}

assert.equal(isDerivedStoreManagerRoleName('StoreManager'), true)
assert.equal(isDerivedStoreManagerRoleName('店长'), true)
assert.equal(isDerivedStoreManagerRoleName('WarehouseManager'), false, '仓库经理可以批量增删')

// 批量分店：只有新增、升级、移除算作会写入的变更；预演为空时不可提交。
assert.equal(countBatchStoreChanges(null), 0)
assert.equal(countBatchStoreChanges({
  dryRun: true,
  addedCount: 2,
  upgradedCount: 1,
  removedCount: 3,
  removedManageableCount: 1,
  unchangedCount: 9,
  protectedManageableCount: 4,
  affectedUserCount: 2,
  usersLosingStoreManagerRole: [],
  usersGainingStoreManagerRole: [],
}), 6, '已存在与受保护的组合不计入变更数')

// 姓名拆分字段：只发有变化的字段；清空发空串；未变化 / 仅空白差异不发，避免把旧数据误写回去。
{
  assert.deepEqual(
    diffUserNameParts(undefined, { familyName: '张', givenName: '伟', middleName: '', nickname: undefined }),
    { familyName: '张', givenName: '伟' },
    '创建用户时只带用户实际填写的字段（空串 / 未填不发）',
  )
  const original = { familyName: '张', givenName: '伟', middleName: null, nickname: 'Wei' }
  assert.deepEqual(
    diffUserNameParts(original, { familyName: '张', givenName: '伟', middleName: '', nickname: 'Wei' }),
    {},
    '原值为 null、表单为空串，以及值完全相同，都不算变化',
  )
  assert.deepEqual(
    diffUserNameParts(original, { familyName: ' 张 ', givenName: '伟', middleName: '', nickname: 'Wei ' }),
    {},
    '首尾空白差异不算变化',
  )
  assert.deepEqual(
    diffUserNameParts(original, { familyName: '李', givenName: '伟', middleName: '', nickname: 'Wei' }),
    { familyName: '李' },
    '只改了姓时只发姓',
  )
  assert.deepEqual(
    diffUserNameParts(original, { familyName: '张', givenName: '', middleName: 'Ming', nickname: '' }),
    { givenName: '', middleName: 'Ming', nickname: '' },
    '清空发空串、新填发新值，未变化的姓不发',
  )
  assert.deepEqual(
    diffUserNameParts(null, { nickname: ' Sam ' }),
    { nickname: 'Sam' },
    '原值整体缺失时新填的昵称按去空白后的值发送',
  )
  assert.deepEqual(
    diffUserNameParts(original, undefined),
    { familyName: '', givenName: '', nickname: '' },
    '表单值整体缺失视为全部清空（原值为 null 的中间名不变）',
  )
  assert.deepEqual(
    pickUserNamePartFormValues({ familyName: '张', givenName: null }),
    { familyName: '张', givenName: '', middleName: '', nickname: '' },
    '回填表单时 null / 缺省显示为空串',
  )
  assert.deepEqual(pickUserNamePartFormValues(undefined), { familyName: '', givenName: '', middleName: '', nickname: '' })
}

assert.equal(getServerErrorMessage({ payload: { message: 'tommy：无权修改该用户' } }, '失败'), 'tommy：无权修改该用户')
assert.equal(getServerErrorMessage(new Error('Network Error'), '失败'), '失败', '没有服务端业务消息时用通用文案')

// 「允许登录」开关：只有后端明确返回 false 才标「不能登录」；缺省（旧后端 / 未查询）不显示任何东西。
{
  assert.equal(isLoginRestricted({ loginEnabled: false }), true, 'loginEnabled === false 才算被限制')
  assert.equal(isLoginRestricted({ loginEnabled: true }), false)
  assert.equal(isLoginRestricted({}), false, '缺省不能当作限制')
  assert.equal(isLoginRestricted({ loginEnabled: null }), false, 'null 不能当作限制')
  assert.equal(isLoginRestricted(null), false)
  assert.equal(isLoginRestricted(undefined), false)

  // 有编辑权且后端返回了值：显示开关；关闭中的账号点一下就是开启，反之亦然。
  assert.deepEqual(
    getLoginToggleState({ canEdit: true, loginEnabled: true, isSelf: false }),
    { visible: true, checked: true, disabled: false, disabledReason: undefined, nextEnabled: false },
  )
  assert.deepEqual(
    getLoginToggleState({ canEdit: true, loginEnabled: false, isSelf: false }),
    { visible: true, checked: false, disabled: false, disabledReason: undefined, nextEnabled: true },
  )
  // 没有 Users.Edit：不显示开关（只剩标签）。
  assert.equal(getLoginToggleState({ canEdit: false, loginEnabled: false, isSelf: false }).visible, false)
  // 状态未知（undefined / null）：不知道当前是否允许，不显示开关，避免把「未查询」当成「允许」去点。
  assert.equal(getLoginToggleState({ canEdit: true, loginEnabled: undefined, isSelf: false }).visible, false)
  assert.equal(getLoginToggleState({ canEdit: true, loginEnabled: null, isSelf: false }).visible, false)
  // 本人账号：开关仍显示但置灰，原因为 self。
  assert.deepEqual(
    getLoginToggleState({ canEdit: true, loginEnabled: true, isSelf: true }),
    { visible: true, checked: true, disabled: true, disabledReason: 'self', nextEnabled: false },
  )

  // 写回新值：只改 guid 匹配的对象，其余（含 null）原样返回；不修改入参。
  const row = { userGUID: 'u1', loginEnabled: false as boolean | undefined }
  const patched = withLoginEnabled(row, 'u1', true)
  assert.deepEqual(patched, { userGUID: 'u1', loginEnabled: true })
  assert.equal(row.loginEnabled, false, '不能修改原对象')
  assert.equal(withLoginEnabled(row, 'other', true), row, 'guid 不匹配时原样返回')
  assert.equal(withLoginEnabled(null, 'u1', true), null)
  assert.deepEqual(withLoginEnabled({ userGUID: 'u2' }, 'u2', false), { userGUID: 'u2', loginEnabled: false }, '原先缺省的也能写入')

  // 成功提示：开启且从没登录过 → 提示还没有可用密码；开启但登录过、或关闭 → 不提示。
  assert.deepEqual(planLoginToggleNotice(true, undefined), { result: 'allowed', showPasswordHint: true })
  assert.deepEqual(planLoginToggleNotice(true, null), { result: 'allowed', showPasswordHint: true })
  assert.deepEqual(planLoginToggleNotice(true, ''), { result: 'allowed', showPasswordHint: true })
  assert.deepEqual(planLoginToggleNotice(true, 'not-a-date'), { result: 'allowed', showPasswordHint: true }, '无法解析按从未登录处理')
  assert.deepEqual(planLoginToggleNotice(true, '2026-10-01T03:04:05'), { result: 'allowed', showPasswordHint: false })
  assert.deepEqual(planLoginToggleNotice(false, undefined), { result: 'restricted', showPasswordHint: false }, '关闭登录不提示密码')
  assert.deepEqual(planLoginToggleNotice(false, '2026-10-01T03:04:05Z'), { result: 'restricted', showPasswordHint: false })

  // 错误码映射：后端四个业务码各有中文提示，大小写 / 首尾空白不敏感，code 字段兜底。
  const failure = (payload: unknown, status = 400) => ({ status, payload })
  assert.equal(resolveLoginToggleErrorKind(failure({ errorCode: 'SELF_PROFILE_FIELDS_DENIED' })), 'self')
  assert.equal(resolveLoginToggleErrorKind(failure({ errorCode: 'USER_NOT_FOUND' }, 404)), 'notFound')
  assert.equal(resolveLoginToggleErrorKind(failure({ errorCode: 'USER_SCOPE_DENIED' }, 403)), 'scopeDenied')
  assert.equal(resolveLoginToggleErrorKind(failure({ errorCode: 'HIGH_PRIVILEGE_TARGET_DENIED' }, 403)), 'highPrivilege')
  assert.equal(resolveLoginToggleErrorKind(failure({ errorCode: ' user_not_found ' })), 'notFound')
  assert.equal(resolveLoginToggleErrorKind(failure({ code: 'USER_SCOPE_DENIED' })), 'scopeDenied')
  assert.equal(resolveLoginToggleErrorKind(failure({ errorCode: 'USER_SCOPE_DENIED', code: 'OTHER' })), 'scopeDenied', 'errorCode 优先于 code')
  // 业务码优先于 HTTP 状态；没有业务码的 403（Users.Edit 策略拒绝）归为无权限。
  assert.equal(resolveLoginToggleErrorKind(failure({ errorCode: 'USER_SCOPE_DENIED' }, 403)), 'scopeDenied')
  assert.equal(resolveLoginToggleErrorKind(failure(undefined, 403)), 'forbidden')
  assert.equal(resolveLoginToggleErrorKind(failure({ message: '禁止访问' }, 403)), 'forbidden')
  // 未知业务码、网络错误、非对象一律通用失败，且不会把原始 message 当文案。
  assert.equal(resolveLoginToggleErrorKind(failure({ errorCode: 'UPDATE_USER_LOGIN_ENABLED_FAILED' }, 200)), 'generic')
  assert.equal(resolveLoginToggleErrorKind(failure({ errorCode: 'constructor' })), 'generic', '原型链上的名字不能命中映射')
  assert.equal(resolveLoginToggleErrorKind(new Error('Network Error')), 'generic')
  assert.equal(resolveLoginToggleErrorKind(undefined), 'generic')
  assert.equal(resolveLoginToggleErrorKind('boom'), 'generic')

  // 每个错误类别都有文案键与中文兜底，且键互不重复。
  const messages = Object.values(LOGIN_TOGGLE_ERROR_MESSAGES)
  assert.equal(messages.length, 6)
  assert.equal(new Set(messages.map((item) => item.key)).size, messages.length, '文案键不能重复')
  assert.ok(messages.every((item) => item.key.startsWith('system.usersWorkspace.loginToggleError.') && item.fallback.length > 0))
}
