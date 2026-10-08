import assert from 'node:assert/strict'
import dayjs from 'dayjs'
import {
  STALE_LOGIN_DAYS,
  countBatchStoreChanges,
  getServerErrorMessage,
  describeLastLogin,
  diffAssignmentKeys,
  diffStoreAssignment,
  getUserDisplayName,
  getUserInitial,
  getUserSecondaryLine,
  isDerivedStoreManagerRoleName,
  mergeSelectedUsers,
  planBatchRoleChange,
  planBatchStatusChange,
  splitVisibleStores,
  toIsActiveQuery,
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

assert.equal(getServerErrorMessage({ payload: { message: 'tommy：无权修改该用户' } }, '失败'), 'tommy：无权修改该用户')
assert.equal(getServerErrorMessage(new Error('Network Error'), '失败'), '失败', '没有服务端业务消息时用通用文案')
