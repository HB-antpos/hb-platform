import {
  createDeviceActivationCode,
  createMobileDeviceActivationCode,
  getDeviceActivationCodes,
  getDeviceActivationManageableStores,
  getMobileDeviceActivationCodes,
  getMobileDeviceActivationManageableAccounts,
  getMobileDeviceActivationManageableStores,
  normalizeDeviceActivationCodeListResponse,
  revokeMobileDeviceActivationCode,
  revokeDeviceActivationCode,
} from './deviceActivationCodeService'

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

async function assertRejects(action: () => Promise<unknown>, label: string) {
  try {
    await action()
  } catch {
    return
  }
  throw new Error(label)
}

const normalized = normalizeDeviceActivationCodeListResponse({
  success: true,
  data: {
    Items: [{
      GrantId: 'grant-1',
      StoreCode: 'S01',
      StoreName: 'Brisbane',
      DeviceSystem: 'Windows',
      Status: 'Available',
      CreatedAtUtc: '2026-08-27T01:00:00Z',
      CreatedBy: 'admin',
      Reason: 'new register',
      ExpiresAtUtc: '2026-08-28T01:00:00Z',
    }],
    Total: 1,
    Page: 1,
    PageSize: 20,
    TotalPages: 1,
  },
})

assertEqual(normalized.items[0]?.grantId, 'grant-1', 'Should normalize grant ID')
assertEqual(normalized.items[0]?.storeName, 'Brisbane', 'Should normalize store name')
assertEqual(normalized.items[0]?.status, 'Available', 'Should normalize grant status')
assertEqual(normalized.total, 1, 'Should normalize pagination total')

// 安卓平板大屏收银的开通码必须保留在列表里，不能因平台未知被丢弃；大小写变体仍按未知丢弃。
const normalizedAndroidTablet = normalizeDeviceActivationCodeListResponse({
  success: true,
  data: {
    Items: [
      {
        GrantId: 'tablet-grant-1',
        StoreCode: 'S01',
        DeviceSystem: 'AndroidTablet',
        Status: 'Available',
        CreatedAtUtc: '2026-08-27T01:00:00Z',
        CreatedBy: 'admin',
        Reason: 'android tablet',
        ExpiresAtUtc: '2026-08-28T01:00:00Z',
      },
      {
        GrantId: 'tablet-grant-2',
        StoreCode: 'S01',
        DeviceSystem: 'androidtablet',
        Status: 'Available',
        CreatedAtUtc: '2026-08-27T01:00:00Z',
        CreatedBy: 'admin',
        Reason: 'bad case',
        ExpiresAtUtc: '2026-08-28T01:00:00Z',
      },
    ],
    Total: 2,
    Page: 1,
    PageSize: 20,
  },
})
assertEqual(normalizedAndroidTablet.items.length, 1, 'Only the exact AndroidTablet grant should be kept')
assertEqual(normalizedAndroidTablet.items[0]?.deviceSystem, 'AndroidTablet', 'AndroidTablet grant should keep its platform')

const normalizedMobile = normalizeDeviceActivationCodeListResponse({
  success: true,
  data: {
    Items: [{
      GrantId: 'mobile-grant-1',
      StoreCode: 'S02',
      StoreName: 'Gold Coast',
      DeviceSystem: 'Android',
      Status: 'Available',
      TargetUserGuid: 'user-guid-1',
      TargetUsername: 'mobile.manager',
      TargetFullName: 'Mobile Manager',
      CreatedAtUtc: '2026-08-31T01:00:00Z',
      CreatedBy: 'admin',
      Reason: 'mobile register',
      ExpiresAtUtc: '2026-09-01T01:00:00Z',
    }],
    Total: 1,
    Page: 1,
    PageSize: 30,
  },
})

assertEqual(normalizedMobile.items[0]?.targetUserGuid, 'user-guid-1', 'Should normalize target user GUID')
assertEqual(normalizedMobile.items[0]?.targetUsername, 'mobile.manager', 'Should normalize target username')
assertEqual(normalizedMobile.items[0]?.targetFullName, 'Mobile Manager', 'Should normalize target full name')

const originalFetch = globalThis.fetch
const calls: Array<{ url: string; method?: string; body?: string }> = []
let rejectNextList = false

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = String(input)
  calls.push({
    url,
    method: init?.method,
    body: typeof init?.body === 'string' ? init.body : undefined,
  })

  if (rejectNextList) {
    rejectNextList = false
    return new Response(JSON.stringify({
      success: false,
      errorCode: 'DEVICE_ACTIVATION_STORE_SCOPE_FORBIDDEN',
      message: 'No manageable stores',
    }), { status: 200, headers: { 'Content-Type': 'application/json' } })
  }

  if (url.endsWith('/manageable-stores')) {
    return new Response(JSON.stringify({
      success: true,
      data: [{ storeCode: 'S01', storeName: 'Brisbane' }],
    }), { status: 200, headers: { 'Content-Type': 'application/json' } })
  }

  if (url.includes('/manageable-accounts')) {
    return new Response(JSON.stringify({
      success: true,
      data: [{ userGuid: 'user-guid-1', username: 'mobile.manager', fullName: 'Mobile Manager' }],
    }), { status: 200, headers: { 'Content-Type': 'application/json' } })
  }

  if (url.endsWith('/revoke') && url.includes('/mobile-device-activation-codes/')) {
    return new Response(JSON.stringify({
      success: true,
      data: {
        grantId: 'mobile-grant-1',
        storeCode: 'S02',
        deviceSystem: 'Android',
        status: 'Revoked',
        targetUserGuid: 'user-guid-1',
        targetUsername: 'mobile.manager',
        targetFullName: 'Mobile Manager',
        createdAtUtc: '2026-08-31T01:00:00Z',
        createdBy: 'admin',
        reason: 'mobile register',
        expiresAtUtc: '2026-09-01T01:00:00Z',
        revokedAtUtc: '2026-08-31T02:00:00Z',
      },
    }), { status: 200, headers: { 'Content-Type': 'application/json' } })
  }

  if (url.endsWith('/revoke')) {
    return new Response(JSON.stringify({
      success: true,
      data: {
        grantId: 'grant-1',
        storeCode: 'S01',
        deviceSystem: 'Windows',
        status: 'Revoked',
        createdAtUtc: '2026-08-27T01:00:00Z',
        createdBy: 'admin',
        reason: 'new register',
        expiresAtUtc: '2026-08-28T01:00:00Z',
        revokedAtUtc: '2026-08-27T02:00:00Z',
      },
    }), { status: 200, headers: { 'Content-Type': 'application/json' } })
  }

  if (init?.method === 'POST' && url.endsWith('/mobile-device-activation-codes')) {
    return new Response(JSON.stringify({
      success: true,
      data: {
        activationCode: 'HBDEV1-22222222222222222222222222-33333333333333333333333333',
        grant: {
          grantId: 'mobile-grant-2',
          storeCode: 'S02',
          storeName: 'Gold Coast',
          deviceSystem: 'Android',
          status: 'Available',
          targetUserGuid: 'user-guid-1',
          targetUsername: 'mobile.manager',
          targetFullName: 'Mobile Manager',
          createdAtUtc: '2026-08-31T01:00:00Z',
          createdBy: 'admin',
          reason: 'new mobile',
          expiresAtUtc: '2026-09-01T01:00:00Z',
        },
      },
    }), { status: 200, headers: { 'Content-Type': 'application/json' } })
  }

  if (init?.method === 'POST') {
    return new Response(JSON.stringify({
      success: true,
      data: {
        activationCode: 'HBDEV1-00000000000000000000000000-11111111111111111111111111',
        grant: {
          grantId: 'grant-2',
          storeCode: 'S01',
          storeName: 'Brisbane',
          deviceSystem: 'iPadOS',
          status: 'Available',
          createdAtUtc: '2026-08-27T01:00:00Z',
          createdBy: 'admin',
          reason: 'new ipad',
          expiresAtUtc: '2026-08-28T01:00:00Z',
        },
      },
    }), { status: 200, headers: { 'Content-Type': 'application/json' } })
  }

  if (url.includes('/mobile-device-activation-codes')) {
    return new Response(JSON.stringify({
      success: true,
      data: {
        items: [{
          grantId: 'mobile-grant-1',
          storeCode: 'S02',
          deviceSystem: 'Android',
          status: 'Available',
          targetUserGuid: 'user-guid-1',
          targetUsername: 'mobile.manager',
          targetFullName: 'Mobile Manager',
          createdAtUtc: '2026-08-31T01:00:00Z',
          createdBy: 'admin',
          reason: 'mobile register',
          expiresAtUtc: '2026-09-01T01:00:00Z',
        }],
        total: 1,
        page: 1,
        pageSize: 30,
      },
    }), { status: 200, headers: { 'Content-Type': 'application/json' } })
  }

  return new Response(JSON.stringify({
    success: true,
    data: {
      items: [{
        grantId: 'grant-1',
        storeCode: 'S01',
        deviceSystem: 'Windows',
        status: 'Available',
        createdAtUtc: '2026-08-27T01:00:00Z',
        createdBy: 'admin',
        reason: 'new register',
        expiresAtUtc: '2026-08-28T01:00:00Z',
      }],
      total: 1,
      page: 2,
      pageSize: 30,
      totalPages: 1,
    },
  }), { status: 200, headers: { 'Content-Type': 'application/json' } })
}) as typeof fetch

try {
  await getDeviceActivationCodes({
    page: 2,
    pageSize: 30,
    storeCode: 'S01',
    deviceSystem: 'Windows',
    status: 'Available',
  })
  const stores = await getDeviceActivationManageableStores()
  const created = await createDeviceActivationCode({
    storeCode: 'S01',
    deviceSystem: 'iPadOS',
    validForMinutes: 1440,
    reason: ' new ipad ',
  })
  const revoked = await revokeDeviceActivationCode('grant-1', ' no longer needed ')

  const mobileList = await getMobileDeviceActivationCodes({
    page: 1,
    pageSize: 30,
    storeCode: 'S02',
    deviceSystem: 'Android',
    status: 'Available',
  })
  const mobileStores = await getMobileDeviceActivationManageableStores()
  const mobileAccounts = await getMobileDeviceActivationManageableAccounts('S02')
  const mobileCreated = await createMobileDeviceActivationCode({
    storeCode: 'S02',
    deviceSystem: 'Android',
    targetUserGuid: 'user-guid-1',
    validForMinutes: 120,
    reason: ' new mobile ',
  })
  const mobileRevoked = await revokeMobileDeviceActivationCode(
    'mobile-grant-1',
    ' rotate device ',
  )

  assertEqual(
    calls[0]?.url,
    '/api/react/v1/device-activation-codes?page=2&pageSize=30&storeCode=S01&deviceSystem=Windows&status=Available',
    'List should use the approved route and filters',
  )
  assertEqual(
    calls[1]?.url,
    '/api/react/v1/device-activation-codes/manageable-stores',
    'Store options should use the scoped route',
  )
  assertEqual(stores[0]?.storeCode, 'S01', 'Store options should be returned')
  assertEqual(
    calls[2]?.body,
    JSON.stringify({
      storeCode: 'S01',
      deviceSystem: 'iPadOS',
      validForMinutes: 1440,
      reason: 'new ipad',
    }),
    'Create should trim reason and preserve the approved TTL',
  )
  assertEqual(created.grant.grantId, 'grant-2', 'Create should normalize the grant')
  assertEqual(created.activationCode.startsWith('HBDEV1-'), true, 'Create should return one-time code')
  assertEqual(
    calls[3]?.url,
    '/api/react/v1/device-activation-codes/grant-1/revoke',
    'Revoke should use the grant route',
  )
  assertEqual(
    calls[3]?.body,
    JSON.stringify({ reason: 'no longer needed' }),
    'Revoke should trim its reason',
  )
  assertEqual(revoked.status, 'Revoked', 'Revoke should return updated status')

  assertEqual(
    calls[4]?.url,
    '/api/react/v1/mobile-device-activation-codes?page=1&pageSize=30&storeCode=S02&deviceSystem=Android&status=Available',
    'Mobile list should use its independent route and filters',
  )
  assertEqual(mobileList.items[0]?.targetUsername, 'mobile.manager', 'Mobile list should preserve target account')
  assertEqual(
    calls[5]?.url,
    '/api/react/v1/mobile-device-activation-codes/manageable-stores',
    'Mobile store options should use the scoped route',
  )
  assertEqual(mobileStores[0]?.storeCode, 'S01', 'Mobile store options should be returned')
  assertEqual(
    calls[6]?.url,
    '/api/react/v1/mobile-device-activation-codes/manageable-accounts?storeCode=S02',
    'Mobile account options should be scoped to the selected store',
  )
  assertEqual(mobileAccounts[0]?.userGuid, 'user-guid-1', 'Mobile account options should be normalized')
  assertEqual(
    calls[7]?.body,
    JSON.stringify({
      storeCode: 'S02',
      deviceSystem: 'Android',
      targetUserGuid: 'user-guid-1',
      validForMinutes: 120,
      reason: 'new mobile',
    }),
    'Mobile create should include the target account and trim its reason',
  )
  assertEqual(mobileCreated.grant.grantId, 'mobile-grant-2', 'Mobile create should normalize the grant')
  assertEqual(
    calls[8]?.url,
    '/api/react/v1/mobile-device-activation-codes/mobile-grant-1/revoke',
    'Mobile revoke should use the mobile grant route',
  )
  assertEqual(calls[8]?.body, JSON.stringify({ reason: 'rotate device' }), 'Mobile revoke should trim reason')
  assertEqual(mobileRevoked.status, 'Revoked', 'Mobile revoke should return updated status')

  rejectNextList = true
  await assertRejects(
    () => getDeviceActivationCodes({ page: 1, pageSize: 30 }),
    'HTTP 200 business rejection must not be normalized into an empty activation-code list',
  )
} finally {
  globalThis.fetch = originalFetch
}

console.log('deviceActivationCodeService.test: ok')
