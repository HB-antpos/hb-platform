import {
  approveAdminSensitiveChangeRequest,
  getAdminSensitiveChangeRequest,
  getAdminSensitiveChangeRequests,
  getAdminEmployeeProfile,
  mapSensitiveStatus,
  rejectAdminSensitiveChangeRequest,
  saveAdminEmployeeProfile,
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

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = String(input)
  calls.push({ url, init })
  const isDetail = /change-requests\/7$/.test(url)
  const data = isDetail
    ? {
        requestId: 7,
        userGuid: 'user-guid-7',
        username: 'employee7',
        status: 'Pending',
        bankAccountNumber: 'full-bank-account',
        bankAccountSummary: '****6789',
        submittedAt: '2026-07-16T00:00:00Z',
        baseSensitiveRevision: 3,
      }
    : /change-requests(\?|$)/.test(url) && init?.method === 'GET'
      ? {
          items: [{
            requestId: 7,
            userGuid: 'user-guid-7',
            username: 'employee7',
            status: 'Pending',
            bankAccountNumber: 'must-not-leak-from-list',
            bankAccountSummary: '****6789',
            bankBsb: '123-456',
            superannuationCompanyName: 'Must Not Leak Super',
            identityIdSummary: '****9999',
            changedFields: ['bankAccountNumber'],
            submittedAt: '2026-07-16T00:00:00Z',
            baseSensitiveRevision: 3,
          }],
          total: 1,
          page: 1,
          pageSize: 20,
        }
      : {
          requestId: 7,
          userGuid: 'user-guid-7',
          username: 'employee7',
          status: url.endsWith('/reject') ? 'Rejected' : 'Approved',
          submittedAt: '2026-07-16T00:00:00Z',
          baseSensitiveRevision: 3,
          sensitiveRevision: 4,
        }

  return new Response(JSON.stringify({ success: true, data }), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  })
}) as typeof fetch

try {
  const list = await getAdminSensitiveChangeRequests({ page: 1, pageSize: 20, status: 'Pending', keyword: 'amy' })
  assertEqual(calls[0]?.init?.method, 'GET', '审核列表应使用 GET')
  assert(calls[0]?.url.includes('/api/EmployeeProfiles/admin/change-requests?'), '审核列表应使用固定后台路径')
  assert(calls[0]?.url.includes('status=Pending'), '审核列表应传递状态筛选')
  assert(calls[0]?.url.includes('search=amy'), '审核列表应传递搜索条件')
  assertEqual(list.items[0]?.status, 'Pending', 'Pending 状态必须保持可审核，不能降级为 Superseded')
  assertEqual(list.items[0]?.changedFields.join(','), 'bankAccountNumber', '列表应直接使用安全 ChangedFields 标识')
  assert(!JSON.stringify(list).includes('must-not-leak-from-list'), '列表映射必须丢弃意外返回的完整账号')
  assert(!JSON.stringify(list).includes('123-456'), '列表映射必须丢弃 BSB')
  assert(!JSON.stringify(list).includes('Must Not Leak Super'), '列表映射必须丢弃养老金公司资料')
  assert(!JSON.stringify(list).includes('****9999'), '列表映射必须丢弃证件摘要')
  assertEqual(calls.length, 1, '列表加载不得调用 request detail 或 employee detail API')

  const detail = await getAdminSensitiveChangeRequest(7)
  assertEqual(calls[1]?.url, '/api/EmployeeProfiles/admin/change-requests/7', '审核详情应使用 requestId 路径')
  assertEqual(detail.bankAccountNumber, 'full-bank-account', '授权详情应保留完整账号用于审核')

  await approveAdminSensitiveChangeRequest(7, { reason: '已核验' })
  assertEqual(calls[2]?.url, '/api/EmployeeProfiles/admin/change-requests/7/approve', '批准应使用固定路径')
  assertEqual(calls[2]?.init?.method, 'POST', '批准应使用 POST')

  await rejectAdminSensitiveChangeRequest(7, { reason: '无法核验' })
  assertEqual(calls[3]?.url, '/api/EmployeeProfiles/admin/change-requests/7/reject', '拒绝应使用固定路径')
  assertEqual(calls[3]?.init?.method, 'POST', '拒绝应使用 POST')
  assertEqual(JSON.parse(String(calls[3]?.init?.body)).reason, '无法核验', '拒绝请求应提交原因')

  await saveAdminEmployeeProfile({
    userGUID: 'user-guid-7',
    bankAccountNumber: 'admin-new',
    confirmSupersedePendingSensitiveChangeRequest: true,
    expectedSensitiveRevision: 4,
  })
  assertEqual(calls[4]?.url, '/api/EmployeeProfiles/admin/user-guid-7', '管理员保存应使用员工路径')
  assertEqual(
    JSON.parse(String(calls[4]?.init?.body)).confirmSupersedePendingSensitiveChangeRequest,
    true,
    '管理员确认重试必须把确认标志传给后端',
  )
  assertEqual(JSON.parse(String(calls[4]?.init?.body)).expectedSensitiveRevision, 4, '管理员保存必须传递敏感 revision')

  const employeeDetail = await getAdminEmployeeProfile('user-guid-7')
  assertEqual(employeeDetail.sensitiveRevision, 4, '管理员详情必须映射敏感 revision')

  assertEqual(mapSensitiveStatus('Withdrawn'), 'Withdrawn', '员工撤回状态必须识别为 Withdrawn')
  assertEqual(mapSensitiveStatus('withdrawn'), 'Withdrawn', '状态映射应忽略大小写')
  assertEqual(mapSensitiveStatus('SomethingNew'), 'Superseded', '未知状态仍按不可操作的 Superseded 处理')

  console.log('employeeProfileService.sensitiveChange.test: ok')
} finally {
  globalThis.fetch = originalFetch
}
