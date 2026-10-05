import assert from 'node:assert/strict'
import dayjs from 'dayjs'
import {
  STALE_LOGIN_DAYS,
  describeLastLogin,
  diffAssignmentKeys,
  diffStoreAssignment,
  getUserDisplayName,
  getUserInitial,
  getUserSecondaryLine,
  splitVisibleStores,
  toIsActiveQuery,
} from './usersPageLogic'

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
