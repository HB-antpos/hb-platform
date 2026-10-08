import {
  activateDevice,
  buildUpdateDeviceRegistrationPayload,
  createEmergencyLoginGrant,
  disableDevice,
  getAppDeviceStatuses,
  getAppDeviceStatusSummary,
  getDeviceRegistrations,
  getEmergencyLoginGrant,
  isDeviceRuntimeOnline,
  lockDevice,
  normalizeAppDeviceStatusListResponse,
  normalizeAppDeviceStatusSummary,
  normalizeAppVersionDistribution,
  normalizeDeviceRegistrationDetail,
  normalizeRegisteredDeviceSystem,
  revokeEmergencyLoginGrant,
} from './deviceRegistrationService'

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)

  if (actualJson !== expectedJson) {
    throw new Error(`${label}. Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

const normalizedChineseDetail = normalizeDeviceRegistrationDetail({
  ID: 12,
  设备硬件识别码: 'HW-CN-01',
  系统设备编号: 'POS-CN-01',
  分店代码: 'S001',
  分店名称: '示例分店',
  设备类型: 'POS',
  设备系统: 'Windows',
  设备状态: 1,
  设备状态描述: '已启用',
  是否允许交易: false,
  备注: '中文字段',
  创建时间: '2026-05-01T10:00:00Z',
  最后修改时间: '2026-05-02T11:00:00Z',
  最后修改人: 'editor-cn',
  创建人: 'creator-cn',
})

assertEqual(normalizedChineseDetail.id, 12, 'Should normalize Chinese ID field')
assertEqual(
  normalizedChineseDetail.hardwareId,
  'HW-CN-01',
  'Should normalize Chinese hardware identifier field',
)
assertEqual(
  normalizedChineseDetail.storeName,
  '示例分店',
  'Should normalize Chinese store name field',
)
assertEqual(
  normalizedChineseDetail.remark,
  '中文字段',
  'Should normalize Chinese remark field',
)
assertEqual(
  normalizedChineseDetail.lastModifiedBy,
  'editor-cn',
  'Should normalize Chinese last modified by field',
)
assertEqual(
  normalizedChineseDetail.allowTransactions,
  false,
  'Should normalize the Chinese transaction permission field',
)

const normalizedCamelDetail = normalizeDeviceRegistrationDetail({
  id: 13,
  hardwareId: 'HW-EN-01',
  systemDeviceNumber: 'POS-EN-01',
  storeCode: 'S002',
  storeName: 'Example Store',
  deviceType: 'Admin',
  deviceSystem: 'Mac',
  status: 3,
  statusDescription: 'Locked',
  AllowTransactions: true,
  remarks: 'camel field',
  createdAt: '2026-05-03T10:00:00Z',
  lastModified: '2026-05-04T11:00:00Z',
  lastModifiedBy: 'editor-en',
  createdBy: 'creator-en',
})

assertEqual(normalizedCamelDetail.id, 13, 'Should normalize camelCase ID field')
assertEqual(
  normalizedCamelDetail.statusDescription,
  'Locked',
  'Should normalize camelCase status description field',
)
assertEqual(
  normalizedCamelDetail.remark,
  'camel field',
  'Should normalize camelCase remark field',
)
assertEqual(
  normalizedCamelDetail.isOnline,
  false,
  'Should normalize missing runtime online field as false'
)
assertEqual(
  normalizedCamelDetail.allowTransactions,
  true,
  'Should normalize the PascalCase transaction permission field',
)
assertEqual(
  normalizeRegisteredDeviceSystem('  '),
  'Windows',
  'Should present legacy blank systems as Windows',
)
assertEqual(
  normalizeRegisteredDeviceSystem(' VisionOS '),
  'VisionOS',
  'Should preserve unknown non-empty systems',
)

const normalizedRuntimeDetail = normalizeDeviceRegistrationDetail({
  id: 14,
  hardwareId: 'HW-RUN-01',
  systemDeviceNumber: 'POS-RUN-01',
  deviceType: 'POS',
  deviceSystem: 'Windows',
  status: 1,
  isOnline: true,
  lastHeartbeatAt: '2026-07-01T10:00:00Z',
  currentCashierId: 'CASHIER-1',
  currentCashierName: 'Alice',
  cashierLoginAt: '2026-07-01T09:55:00Z',
  appVersion: '1.0.51',
})

assertEqual(normalizedRuntimeDetail.isOnline, true, 'Should normalize runtime online status')
assertEqual(
  normalizedRuntimeDetail.appVersion,
  '1.0.51',
  'Should normalize the client app version reported with heartbeats',
)
assertEqual(
  normalizedCamelDetail.appVersion ?? null,
  null,
  'Should leave the app version empty when the client never reported one',
)
assertEqual(
  normalizedRuntimeDetail.lastHeartbeatAt,
  '2026-07-01T10:00:00Z',
  'Should normalize last heartbeat time',
)
assertEqual(
  normalizedRuntimeDetail.currentCashierName,
  'Alice',
  'Should normalize current cashier name',
)
assertEqual(
  normalizedRuntimeDetail.allowTransactions,
  true,
  'Missing transaction permission should default to allowed',
)
assertEqual(
  isDeviceRuntimeOnline(
    normalizedRuntimeDetail,
    Date.parse('2026-07-01T10:00:44Z')
  ),
  true,
  'Runtime status should stay online inside the 45 second heartbeat window',
)
assertEqual(
  isDeviceRuntimeOnline(
    normalizedRuntimeDetail,
    Date.parse('2026-07-01T10:00:46Z')
  ),
  false,
  'Runtime status should become offline after the 45 second heartbeat window',
)

const updateValuesWithRuntimeExtras = {
  deviceType: 'Mobile',
  deviceSystem: 'Android',
  remark: 'updated',
  allowTransactions: false,
  status: 2,
  statusDescription: 'Disabled',
}

assertDeepEqual(
  buildUpdateDeviceRegistrationPayload(updateValuesWithRuntimeExtras),
  {
    设备类型: 'Mobile',
    设备系统: 'Android',
    是否允许交易: false,
    备注: 'updated',
  },
  'Update payload should only include editable Chinese DTO fields',
)

const normalizedAppDevices = normalizeAppDeviceStatusListResponse({
  success: true,
  data: {
    items: [{
      Id: 'F53AF6E9-A4C6-4C31-9B19-83E93B120D93',
      HardwareId: 'HW-APP-01',
      SystemDeviceNumber: 'DEV202607080001',
      DeviceSystem: 'Android',
      AppVersion: '1.2.3',
      AppBuildVersion: '45',
      RuntimeVersion: '1.2.3',
      Channel: 'production',
      UpdateId: '12345678-90ab-cdef-1234-567890abcdef',
      UpdateSource: 'ota',
      LastSeenAtUtc: '2026-07-08T09:00:00Z',
      IsOnline: 'true',
      LastSeenUsername: 'ada',
    }],
    Total: 1,
    Page: 1,
    PageSize: 20,
  },
})

assertEqual(normalizedAppDevices.total, 1, 'Should normalize App device total')
assertEqual(normalizedAppDevices.devices[0]?.hardwareId, 'HW-APP-01', 'Should normalize App hardware ID')
assertEqual(normalizedAppDevices.devices[0]?.isOnline, true, 'Should normalize App online state')
assertEqual(normalizedAppDevices.devices[0]?.appVersion, '1.2.3', 'Should normalize App package version')
assertEqual(normalizedAppDevices.devices[0]?.appBuildVersion, '45', 'Should normalize App build version')
assertEqual(normalizedAppDevices.devices[0]?.runtimeVersion, '1.2.3', 'Should normalize App runtime version')
assertEqual(normalizedAppDevices.devices[0]?.channel, 'production', 'Should normalize App channel')
assertEqual(
  normalizedAppDevices.devices[0]?.updateId,
  '12345678-90ab-cdef-1234-567890abcdef',
  'Should preserve full App update ID',
)
assertEqual(normalizedAppDevices.devices[0]?.updateSource, 'ota', 'Should normalize App update source')
assertEqual(normalizedAppDevices.devices[0]?.lastSeenUsername, 'ada', 'Should normalize App recent user')

assertDeepEqual(
  normalizeAppDeviceStatusSummary({
    success: true,
    data: {
      Total: 3,
      Online: 1,
      Offline: 2,
      Android: 2,
      Ios: 1,
      UnknownSystem: 0,
    },
  }),
  {
    total: 3,
    online: 1,
    offline: 2,
    android: 2,
    ios: 1,
    unknownSystem: 0,
  },
  'Should normalize App device summary',
)

assertDeepEqual(
  normalizeAppVersionDistribution({
    success: true,
    data: {
      Total: 3,
      Items: [
        { DeviceSystem: 'iOS', AppVersion: '1.0.7', AppBuildVersion: '58', Total: 2, Online: 1, Ota: 1, Embedded: 1 },
        { DeviceSystem: null, AppVersion: null, AppBuildVersion: null, Total: 1, Online: 0, Ota: 0, Embedded: 0 },
        null,
      ],
    },
  }),
  {
    total: 3,
    items: [
      { deviceSystem: 'iOS', appVersion: '1.0.7', appBuildVersion: '58', total: 2, online: 1, ota: 1, embedded: 1 },
      { total: 1, online: 0, ota: 0, embedded: 0 },
    ],
  },
  'Should normalize App version distribution, dropping invalid rows and null versions',
)
assertDeepEqual(
  normalizeAppVersionDistribution({ success: true, data: null }),
  { total: 0, items: [] },
  'Should tolerate an empty App version distribution payload',
)

const originalFetch = globalThis.fetch
const calls: Array<{ url: string; method?: string; body?: string }> = []

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = String(input)
  calls.push({
    url,
    method: init?.method,
    body: typeof init?.body === 'string' ? init.body : undefined,
  })

  if (url.includes('/api/mobile/app-device-status/paged')) {
    return new Response(JSON.stringify({
      success: true,
      data: {
        items: [{ id: 'app-1', hardwareId: 'HW-APP-URL', isOnline: true }],
        total: 1,
        page: 1,
        pageSize: 20,
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }

  if (url.includes('/api/mobile/app-device-status/summary')) {
    return new Response(JSON.stringify({
      success: true,
      data: {
        total: 1,
        online: 1,
        offline: 0,
        android: 1,
        ios: 0,
        unknownSystem: 0,
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }

  if (url.includes('/api/react/v1/emergency-login-grants')) {
    const revoked = url.endsWith('/revoke')
    const body = init?.body ? JSON.parse(String(init.body)) as Record<string, unknown> : {}
    const grant = {
      grantId: 'grant-1',
      storeCode: 'S01',
      businessDate: '2026-07-14',
      keyId: 'KEY1',
      permissionProfile: 'AllPosTerminal',
      issuedBy: 'admin',
      reason: String(body.reason ?? 'outage'),
      issuedAtUtc: '2026-07-14T01:00:00Z',
      expiresAtUtc: '2026-07-14T14:00:00Z',
      status: revoked ? 'Revoked' : 'Active',
    }
    const data = init?.method === 'POST' && !revoked
      ? { grant, token: 'HBPOSE1-KEY1-PAYLOAD-SIGNATURE' }
      : grant

    return new Response(JSON.stringify({ success: true, data }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }

  return new Response(JSON.stringify({
    success: true,
    data: {
      devices: [
        { id: 21, hardwareId: 'HW-LEGACY', deviceSystem: '', AllowTransactions: false, AppVersion: '1.0.51' },
        { id: 22, hardwareId: 'HW-UNKNOWN', deviceSystem: 'VisionOS' },
      ],
      pagination: {
        page: 2,
        pageSize: 30,
        total: 2,
        totalPages: 1,
      },
    },
  }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}) as typeof fetch

try {
  const registeredDevices = await getDeviceRegistrations({
    page: 2,
    pageSize: 30,
    storeCode: 'S01',
    deviceType: 'POS',
    deviceSystem: 'Other',
  })
  await activateDevice(12)
  await disableDevice(12)
  await lockDevice(12)
  await getAppDeviceStatuses({
    page: 1,
    pageSize: 20,
    storeCode: 'S01',
    deviceSystem: 'Android',
    onlineState: 'online',
    keyword: 'Ada',
  })
  await getAppDeviceStatusSummary({
    storeCode: 'S01',
    deviceSystem: 'Android',
    keyword: 'Ada',
  })
  const grant = await getEmergencyLoginGrant('S01')
  const createdGrant = await createEmergencyLoginGrant('S01', ' network outage ')
  const revokedGrant = await revokeEmergencyLoginGrant('grant-1', ' resolved ')

  assertEqual(
    calls[0]?.url,
    '/api/paged?page=2&pageSize=30&storeCode=S01&deviceType=POS&deviceSystem=Other',
    'Device registration list should use legacy device API base path',
  )
  assertEqual(calls[0]?.method, 'GET', 'Device registration list should use GET')
  assertEqual(
    registeredDevices.devices[0]?.deviceSystem,
    'Windows',
    'Device registration list should normalize legacy blank systems',
  )
  assertEqual(
    registeredDevices.devices[1]?.deviceSystem,
    'VisionOS',
    'Device registration list should preserve unknown systems',
  )
  assertEqual(
    registeredDevices.devices[0]?.allowTransactions,
    false,
    'Device registration list should normalize a disabled transaction permission',
  )
  assertEqual(
    registeredDevices.devices[1]?.allowTransactions,
    true,
    'Device registration list should default missing transaction permission to allowed',
  )
  assertEqual(
    registeredDevices.devices[0]?.appVersion,
    '1.0.51',
    'Device registration list should expose the reported client app version',
  )
  assertEqual(
    registeredDevices.devices[1]?.appVersion ?? null,
    null,
    'Device registration list should leave the app version empty for devices that never reported one',
  )
  assertEqual(
    calls[1]?.url,
    '/api/12/activate',
    'Device activation should use legacy device API base path',
  )
  assertEqual(calls[1]?.method, 'POST', 'Device activation should use POST')
  assertEqual(
    calls[2]?.url,
    '/api/12/disable',
    'Device disable should use legacy device API base path',
  )
  assertEqual(
    calls[3]?.url,
    '/api/12/lock',
    'Device lock should use legacy device API base path',
  )
  assertEqual(
    calls[4]?.url,
    '/api/mobile/app-device-status/paged?page=1&pageSize=20&storeCode=S01&deviceSystem=Android&onlineState=online&keyword=Ada',
    'App device list should use mobile app-device-status paged API',
  )
  assertEqual(calls[4]?.method, 'GET', 'App device list should use GET')
  assertEqual(
    calls[5]?.url,
    '/api/mobile/app-device-status/summary?storeCode=S01&deviceSystem=Android&keyword=Ada',
    'App device summary should use mobile app-device-status summary API',
  )
  assertEqual(calls[5]?.method, 'GET', 'App device summary should use GET')
  assertEqual(
    calls[6]?.url,
    '/api/react/v1/emergency-login-grants?storeCode=S01',
    'Emergency grant summary should use the store query route',
  )
  assertEqual(grant?.status, 'Active', 'Emergency grant summary should be normalized')
  assertEqual(calls[7]?.method, 'POST', 'Emergency grant creation should use POST')
  assertEqual(
    calls[7]?.body,
    JSON.stringify({ storeCode: 'S01', reason: 'network outage' }),
    'Emergency grant creation should trim its reason',
  )
  assertEqual(
    createdGrant.token,
    'HBPOSE1-KEY1-PAYLOAD-SIGNATURE',
    'Emergency grant creation should return the one-time token',
  )
  assertEqual(
    calls[8]?.url,
    '/api/react/v1/emergency-login-grants/grant-1/revoke',
    'Emergency grant revocation should use the grant route',
  )
  assertEqual(revokedGrant.status, 'Revoked', 'Emergency grant revocation should return its status')
} finally {
  globalThis.fetch = originalFetch
}

console.log('deviceRegistrationService.test: ok')
