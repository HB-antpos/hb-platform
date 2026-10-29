import {
  getManagedEmployeeProfile,
  getManagedEmployeeProfileFilterOptions,
  getManagedEmployeeProfiles,
  saveManagedEmployeeProfile,
} from './employeeProfileService'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

const originalFetch = globalThis.fetch
const calls: Array<{ url: string; init?: RequestInit }> = []

const detail = {
  userGUID: 'user-guid-1',
  username: 'kevin',
  email: 'kevin@example.com',
  hasProfile: true,
  storeNames: ['Wallsend'],
  roleNames: ['StoreStaff'],
  phone: '0412 345 678',
  gender: 'male',
  employmentType: 'partTime',
  address: '1 Old St',
  bankBsb: '062-000',
  bankAccountNumber: '****6789',
  identityId: '****7731',
  hasBirthday: true,
  canEditEmploymentType: false,
}

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = String(input)
  calls.push({ url, init })
  const data = url.includes('/managed/filter-options')
    ? {
        stores: [{ storeGuid: 's1', storeCode: 'W', storeName: 'Wallsend' }],
        roles: [{ roleGuid: 'r1', roleName: 'StoreStaff' }],
      }
    : /\/managed(\?|$)/.test(url)
      ? {
          items: [{
            userGUID: 'user-guid-1',
            username: 'kevin',
            fullName: 'Kevin W',
            hasProfile: true,
            bankBsb: '062-000',
            bankAccountNumber: '****6789',
            employeeType: 'casual',
            storeNames: ['Wallsend', 'Kotara'],
            roleNames: ['StoreStaff'],
          }],
          total: 1,
          page: 1,
          pageSize: 20,
        }
      : detail
  return new Response(JSON.stringify({ success: true, data }), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  })
}) as typeof fetch

try {
  const list = await getManagedEmployeeProfiles({
    page: 1,
    pageSize: 20,
    keyword: 'kev',
    storeGuid: 's1',
    roleGuid: 'r1',
  })
  assertEqual(calls[0]?.init?.method, 'GET', '列表应使用 GET')
  assert(calls[0]?.url.includes('/api/EmployeeProfiles/managed?'), '列表应走 /managed 接口')
  for (const part of ['search=kev', 'storeGuid=s1', 'roleGuid=r1', 'page=1', 'pageSize=20']) {
    assert(calls[0]?.url.includes(part), `列表应带上过滤参数 ${part}`)
  }
  assertEqual(list.items[0]?.userGUID, 'user-guid-1', '列表项应保留 userGUID')
  assertEqual(list.items[0]?.displayName, 'Kevin W', '列表项应映射姓名')
  assertEqual(list.items[0]?.employmentType, 'casual', '列表项应映射员工类型')
  assertEqual(list.items[0]?.storeNames?.join('|'), 'Wallsend|Kotara', '列表项应带分店名称')
  assertEqual(list.items[0]?.roleNames?.join('|'), 'StoreStaff', '列表项应带角色名称')

  const options = await getManagedEmployeeProfileFilterOptions()
  assertEqual(calls[1]?.url.includes('/api/EmployeeProfiles/managed/filter-options'), true, '筛选项应走固定路径')
  assertEqual(options.stores[0]?.storeName, 'Wallsend', '应返回分店选项')
  assertEqual(options.roles[0]?.roleName, 'StoreStaff', '应返回角色选项')

  const managed = await getManagedEmployeeProfile('user-guid-1')
  assert(calls[2]?.url.endsWith('/api/EmployeeProfiles/managed/user-guid-1'), '详情应使用员工路径')
  assertEqual(managed.hasBirthday, true, '应映射生日是否已填')
  assertEqual(managed.canEditEmploymentType, false, '应映射职位类型编辑能力')
  assertEqual(managed.bankAccountNumber, '****6789', '敏感资料只有脱敏摘要')

  await saveManagedEmployeeProfile('user-guid-1', { phone: '0400 000 111', address: '' })
  assertEqual(calls[3]?.init?.method, 'PUT', '保存应使用 PUT')
  assert(calls[3]?.url.endsWith('/api/EmployeeProfiles/managed/user-guid-1'), '保存应使用员工路径')
  const body = JSON.parse(String(calls[3]?.init?.body))
  assertEqual(JSON.stringify(body), JSON.stringify({ phone: '0400 000 111', address: '' }), '保存只提交改动过的非敏感字段')
} finally {
  globalThis.fetch = originalFetch
}

console.log('employeeProfileService.managed.test: ok')
