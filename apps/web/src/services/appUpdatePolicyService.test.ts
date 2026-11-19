import {
  createAppUpdatePolicyService,
  type AppUpdatePolicyTransport,
} from './appUpdatePolicyService'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${message}: expected ${expectedJson}, got ${actualJson}`)
  }
}

type TransportCall = {
  method: 'get' | 'post' | 'put' | 'delete'
  url: string
  payload?: unknown
  params?: Record<string, unknown>
  signal?: AbortSignal
}

const calls: TransportCall[] = []
const transport: AppUpdatePolicyTransport = {
  async get(url, options) {
    calls.push({
      method: 'get',
      url,
      params: options?.params,
      signal: options?.signal,
    })
    if (url === '/api/app-update-releases/ios') {
      const app = options?.params?.app === 'pos-handheld' ? 'pos-handheld' : 'mobile-ios'
      return {
        success: true,
        data: [{
          id: 'release-mobile',
          app,
          appStoreId: '6786073002',
          bundleIdentifier: 'com.hotbargain.mobile',
          version: '1.2.0',
          buildNumber: '28',
          storefront: 'au',
          appStoreUrl: 'https://apps.apple.com/au/app/id6786073002',
          appleVerifiedAtUtc: '2026-07-30T00:00:00Z',
          createdAt: '2026-07-30T00:00:00Z',
          createdBy: 'admin',
        }],
      }
    }
    if (url === '/api/app-update-policies/pos-ipad/store-options') {
      return {
        success: true,
        data: [{ storeGuid: 'store-1', storeCode: '001', storeName: 'Brisbane' }],
      }
    }
    if (url === '/api/pos-ipad/ota-releases') {
      return {
        success: true,
        data: [{
          id: 'ota-1',
          environment: 'production',
          updateGroupId: '11111111-1111-1111-1111-111111111111',
          iosUpdateId: '22222222-2222-2222-2222-222222222222',
          channel: 'pos-ipad-production',
          runtimeVersion: '1.0.0',
          gitCommitHash: 'abcdef1',
          dashboardUrl: 'https://expo.dev/accounts/hb/projects/pos-ipad/updates/ota-1',
          publishedAtUtc: '2026-07-30T00:00:00Z',
          isRollback: false,
          rollbackOfReleaseId: null,
          createdAt: '2026-07-30T00:00:00Z',
          createdBy: 'release-bot',
        }],
      }
    }
    if (url === '/api/pos-ipad/ota-assignments') {
      return {
        success: true,
        data: [
          {
            id: 'assign-store',
            targetType: 'store',
            storeGuid: 'store-1',
            storeCode: '001',
            storeName: 'Brisbane',
            deviceRegistrationId: null,
            enabled: true,
            expiresAtUtc: '2026-12-01T00:00:00',
            isExpired: false,
            policyVersion: '7',
            releaseId: 'ota-1',
            release: { id: 'ota-1', channel: 'pos-ipad-production', runtimeVersion: '1.0.0' },
            forceUpdate: true,
            releaseMessage: ' 请升级 ',
            note: '  ',
            updatedAt: '2026-11-19T01:00:00Z',
            updatedBy: 'admin',
          },
          {
            id: 'assign-device',
            targetType: 'device',
            storeCode: '002',
            deviceRegistrationId: 42,
            deviceNumber: 'POS_1002_0001',
            deviceRemarks: '前台 1 号',
            deviceAppVersion: '1.0.3',
            enabled: false,
            isExpired: true,
            policyVersion: 3,
            releaseId: 'ota-1',
            release: null,
          },
          // 未知 targetType / 缺字段 / 非对象都要被防御式归一化，不能让页面崩溃。
          { id: 'assign-weird', targetType: 'galaxy', deviceRegistrationId: -5 },
        ],
      }
    }
    if (url === '/api/pos-ipad/ota-assignments/target-options/devices') {
      return {
        success: true,
        data: {
          items: [
            {
              deviceRegistrationId: 42,
              systemDeviceNumber: 'POS_1002_0001',
              storeCode: '002',
              storeName: 'Sydney',
              remarks: '前台 1 号',
              appVersion: '1.0.3',
              isOnline: true,
              lastHeartbeatAt: '2026-11-19T00:59:00Z',
            },
            // 没有有效注册 ID 的行必须被丢弃。
            { systemDeviceNumber: 'BROKEN' },
            'garbage',
          ],
          total: 31,
          page: 2,
          pageSize: 20,
        },
      }
    }
    if (url === '/api/app-update-policies/mobile-android') {
      return {
        success: true,
        data: {
          enabled: true,
          minimumSupportedBuildNumber: 60,
          releaseMessage: ' 请安装新版 ',
          policyVersion: '3',
          updatedAt: '2026-10-05T01:00:00Z',
          updatedBy: 'admin',
          latestBuild: {
            easBuildId: 'build-63',
            appVersion: '1.0.10',
            appBuildVersion: 63,
            completedAt: '2026-10-03T05:06:00Z',
          },
        },
      }
    }
    if (url === '/api/pos-ipad/ota-rollout') {
      return {
        success: true,
        data: {
          id: 'rollout-1',
          enabled: true,
          policyVersion: 3,
          releaseId: 'ota-1',
          forceUpdate: true,
          targetScope: 'stores',
          targetStoreGuids: ['store-1'],
          releaseMessage: '升级后继续',
          release: null,
          updatedAt: '2026-07-30T01:00:00Z',
          updatedBy: 'admin',
        },
      }
    }
    return {
      success: true,
      data: {
        id: null,
        enabled: false,
        policyVersion: 0,
        releaseId: null,
        latestVersion: null,
        minimumSupportedVersion: null,
        minimumSupportedBuildNumber: null,
        appStoreUrl: null,
        releaseMessage: null,
        targetScope: 'all',
        targetStoreGuids: [],
        updatedAt: null,
        updatedBy: null,
      },
    }
  },
  async post(url, payload, options) {
    calls.push({ method: 'post', url, payload, signal: options?.signal })
    if (url === '/api/pos-ipad/ota-assignments') {
      return {
        success: true,
        data: { id: 'assign-new', targetType: 'device', deviceRegistrationId: 42, policyVersion: 1 },
      }
    }
    return {
      success: true,
      data: {
        id: 'release-mobile',
        app: 'mobile-ios',
        appStoreId: '6786073002',
        bundleIdentifier: 'com.hotbargain.mobile',
        version: '1.2.0',
        buildNumber: '28',
        storefront: 'au',
        appStoreUrl: 'https://apps.apple.com/au/app/id6786073002',
        appleVerifiedAtUtc: '2026-07-30T00:00:00Z',
        createdAt: '2026-07-30T00:00:00Z',
        createdBy: 'admin',
      },
    }
  },
  async delete(url, options) {
    calls.push({
      method: 'delete',
      url,
      params: options?.params,
      signal: options?.signal,
    })
    return { success: true, data: true }
  },
  async put(url, payload, options) {
    calls.push({ method: 'put', url, payload, signal: options?.signal })
    if (url.startsWith('/api/pos-ipad/ota-assignments/')) {
      return {
        success: true,
        data: { id: 'assign-store', targetType: 'store', policyVersion: 8, releaseId: 'ota-2' },
      }
    }
    if (url === '/api/app-update-policies/mobile-android') {
      // 后端返回无公开包、策略已停用的默认形态，校验 latestBuild=null 的规范化。
      return {
        success: true,
        data: {
          enabled: false,
          minimumSupportedBuildNumber: null,
          releaseMessage: null,
          policyVersion: 4,
          updatedAt: null,
          updatedBy: null,
          latestBuild: null,
        },
      }
    }
    return {
      success: true,
      data: url === '/api/pos-ipad/ota-rollout'
        ? {
            id: 'rollout-1',
            enabled: true,
            policyVersion: 4,
            releaseId: 'ota-1',
            forceUpdate: true,
            targetScope: 'stores',
            targetStoreGuids: ['store-1'],
            releaseMessage: '升级后继续',
            release: null,
            updatedAt: '2026-07-30T02:00:00Z',
            updatedBy: 'admin',
          }
        : {
            id: 'policy-1',
            enabled: true,
            policyVersion: 2,
            releaseId: 'release-mobile',
            latestVersion: '1.2.0',
            minimumSupportedVersion: '1.1.0',
            minimumSupportedBuildNumber: null,
            appStoreUrl: 'https://apps.apple.com/au/app/id6786073002',
            releaseMessage: '请升级',
            targetScope: 'all',
            targetStoreGuids: [],
            updatedAt: '2026-07-30T02:00:00Z',
            updatedBy: 'admin',
          },
    }
  },
}

async function run() {
  const service = createAppUpdatePolicyService(transport)
  const controller = new AbortController()

  const releases = await service.getIosAppStoreReleases('mobile-ios', controller.signal)
  assertEqual(releases[0]?.app, 'mobile-ios', 'App Store 发布事实应解包标准 ApiResponse')
  assertDeepEqual(
    calls[calls.length - 1],
    {
      method: 'get',
      url: '/api/app-update-releases/ios',
      params: { app: 'mobile-ios', storefront: 'au' },
      signal: controller.signal,
    },
    '发布事实查询必须按 app 和 AU storefront 隔离，并透传 AbortSignal',
  )

  const handheldReleases = await service.getIosAppStoreReleases('pos-handheld')
  assertEqual(
    handheldReleases[0]?.app,
    'pos-handheld',
    '手持 iOS App Store 发布事实必须保留独立 app 身份',
  )
  assertDeepEqual(
    calls[calls.length - 1]?.params,
    { app: 'pos-handheld', storefront: 'au' },
    '手持 iOS 发布事实查询必须按 pos-handheld 隔离',
  )

  await service.createIosAppStoreRelease({
    app: 'mobile-ios',
    appStoreId: '6786073002',
    buildNumber: '28',
    storefront: 'au',
  })
  assertEqual(
    calls[calls.length - 1]?.url,
    '/api/app-update-releases/ios',
    'Web 只调用后台 Apple Lookup 登记入口',
  )

  await service.getMobileIosNativePolicy(controller.signal)
  assertEqual(calls[calls.length - 1]?.url, '/api/app-update-policies/mobile-ios', 'Mobile 策略 GET 路径应固定')
  assertEqual(
    calls[calls.length - 1]?.signal,
    controller.signal,
    'Mobile 策略 GET 必须透传 AbortSignal',
  )

  await service.saveMobileIosNativePolicy({
    expectedPolicyVersion: 1,
    enabled: true,
    releaseId: 'release-mobile',
    minimumSupportedVersion: '1.1.0',
    minimumSupportedBuildNumber: 31,
    releaseMessage: '请升级',
  })
  assertDeepEqual(
    calls[calls.length - 1],
    {
      method: 'put',
      url: '/api/app-update-policies/mobile-ios',
      payload: {
        expectedPolicyVersion: 1,
        enabled: true,
        releaseId: 'release-mobile',
        minimumSupportedVersion: '1.1.0',
        minimumSupportedBuildNumber: 31,
        releaseMessage: '请升级',
      },
    },
    'Mobile 策略 PUT 必须透传最低支持构建号及后台 DTO 字段',
  )

  await service.getPosIpadNativePolicy()
  assertEqual(calls[calls.length - 1]?.url, '/api/app-update-policies/pos-ipad/native', 'iPad 原生策略路径应固定')

  await service.savePosIpadNativePolicy({
    expectedPolicyVersion: 2,
    enabled: true,
    releaseId: 'release-ipad',
    minimumSupportedVersion: '2.0.0',
    minimumSupportedBuildNumber: 28,
    releaseMessage: '请升级',
    targetScope: 'all',
    targetStoreGuids: [],
  })
  assertDeepEqual(
    calls[calls.length - 1]?.payload,
    {
      expectedPolicyVersion: 2,
      enabled: true,
      releaseId: 'release-ipad',
      minimumSupportedVersion: '2.0.0',
      minimumSupportedBuildNumber: 28,
      releaseMessage: '请升级',
      targetScope: 'all',
      targetStoreGuids: [],
    },
    'iPad 原生策略 PUT 必须携带策略版本和最低支持构建号',
  )

  const stores = await service.getPosIpadStoreOptions()
  assertEqual(stores[0]?.storeGuid, 'store-1', '分店选项必须使用可信 Store GUID')

  const otaReleases = await service.getPosIpadOtaReleases()
  assertEqual(otaReleases[0]?.iosUpdateId, '22222222-2222-2222-2222-222222222222', 'OTA 发布应保留 iOS update ID')

  const rollout = await service.getPosIpadOtaRollout()
  assertEqual(rollout.forceUpdate, true, 'OTA rollout 应保留强制更新开关')

  await service.savePosIpadOtaRollout({
    expectedPolicyVersion: 3,
    enabled: true,
    releaseId: 'ota-1',
    forceUpdate: true,
    targetScope: 'stores',
    targetStoreGuids: ['store-1'],
    releaseMessage: '升级后继续',
  })
  assertEqual(calls[calls.length - 1]?.url, '/api/pos-ipad/ota-rollout', 'OTA rollout PUT 路径应固定')

  const androidPolicy = await service.getMobileAndroidNativePolicy(controller.signal)
  assertDeepEqual(
    calls[calls.length - 1],
    {
      method: 'get',
      url: '/api/app-update-policies/mobile-android',
      params: undefined,
      signal: controller.signal,
    },
    'Mobile 安卓原生策略 GET 路径应固定并透传 AbortSignal',
  )
  assertDeepEqual(
    androidPolicy,
    {
      enabled: true,
      minimumSupportedBuildNumber: 60,
      releaseMessage: '请安装新版',
      policyVersion: 3,
      updatedAt: '2026-10-05T01:00:00Z',
      updatedBy: 'admin',
      latestBuild: {
        easBuildId: 'build-63',
        appVersion: '1.0.10',
        appBuildVersion: 63,
        completedAt: '2026-10-03T05:06:00Z',
      },
    },
    'Mobile 安卓原生策略必须规范化为数字策略版本、整数构建号与公开安卓包',
  )

  const savedAndroidPolicy = await service.saveMobileAndroidNativePolicy({
    expectedPolicyVersion: 3,
    enabled: true,
    minimumSupportedBuildNumber: 63,
    releaseMessage: '请安装新版',
  })
  assertDeepEqual(
    calls[calls.length - 1],
    {
      method: 'put',
      url: '/api/app-update-policies/mobile-android',
      payload: {
        expectedPolicyVersion: 3,
        enabled: true,
        minimumSupportedBuildNumber: 63,
        releaseMessage: '请安装新版',
      },
    },
    'Mobile 安卓原生策略 PUT 必须只携带契约约定的四个字段',
  )
  assertEqual(savedAndroidPolicy.latestBuild, null, '无公开安卓包时 latestBuild 必须为 null')
  assertEqual(savedAndroidPolicy.policyVersion, 4, 'PUT 返回的策略版本必须回填')

  const malformedAndroidService = createAppUpdatePolicyService({
    ...transport,
    async get() {
      return {
        success: true,
        data: {
          enabled: false,
          latestBuild: { easBuildId: 'build-x', appVersion: '1.0.0', appBuildVersion: 0 },
        },
      }
    },
  })
  const malformedAndroidPolicy = await malformedAndroidService.getMobileAndroidNativePolicy()
  assertEqual(
    malformedAndroidPolicy.latestBuild,
    null,
    '公开安卓包构建号非正整数时必须视为无可用公开包',
  )
  assertEqual(malformedAndroidPolicy.policyVersion, 0, '缺失策略版本时回退为 0')

  // ---- iPad OTA 覆盖层 ----
  const assignments = await service.getPosIpadOtaAssignments(controller.signal)
  assertDeepEqual(
    calls[calls.length - 1],
    {
      method: 'get',
      url: '/api/pos-ipad/ota-assignments',
      params: undefined,
      signal: controller.signal,
    },
    'OTA 覆盖列表 GET 路径应固定并透传 AbortSignal',
  )
  assertEqual(assignments.length, 3, '覆盖列表每一行都应被归一化')
  assertDeepEqual(
    assignments[0],
    {
      id: 'assign-store',
      targetType: 'store',
      storeGuid: 'store-1',
      storeCode: '001',
      storeName: 'Brisbane',
      deviceRegistrationId: null,
      deviceNumber: null,
      deviceRemarks: null,
      deviceAppVersion: null,
      enabled: true,
      expiresAtUtc: '2026-12-01T00:00:00',
      isExpired: false,
      policyVersion: 7,
      releaseId: 'ota-1',
      release: {
        id: 'ota-1',
        environment: '',
        updateGroupId: '',
        iosUpdateId: '',
        channel: 'pos-ipad-production',
        runtimeVersion: '1.0.0',
        gitCommitHash: null,
        dashboardUrl: null,
        publishedAtUtc: '',
        isRollback: false,
        rollbackOfReleaseId: null,
        createdAt: '',
        createdBy: null,
      },
      forceUpdate: true,
      releaseMessage: '请升级',
      note: null,
      updatedAt: '2026-11-19T01:00:00Z',
      updatedBy: 'admin',
    },
    '分店覆盖必须规范化为数字策略版本、去空白文案，并保留内嵌发布',
  )
  assertEqual(assignments[1]?.targetType, 'device', '设备覆盖应保留 device 类型')
  assertEqual(assignments[1]?.deviceRegistrationId, 42, '设备覆盖应保留设备注册 ID')
  assertEqual(assignments[1]?.isExpired, true, '已过期标记应保留')
  assertEqual(assignments[1]?.release, null, '缺少内嵌发布时 release 必须为 null')
  assertEqual(assignments[2]?.targetType, 'store', '未知目标类型回退为 store')
  assertEqual(assignments[2]?.deviceRegistrationId, null, '负数设备注册 ID 必须视为无效')
  assertEqual(assignments[2]?.policyVersion, 0, '缺失策略版本回退为 0')

  const devicePage = await service.getPosIpadOtaTargetDevices(
    { storeGuid: 'store-1', keyword: '  1002 ', page: 2, pageSize: 20 },
    controller.signal,
  )
  assertDeepEqual(
    calls[calls.length - 1],
    {
      method: 'get',
      url: '/api/pos-ipad/ota-assignments/target-options/devices',
      params: { storeGuid: 'store-1', keyword: '1002', page: 2, pageSize: 20 },
      signal: controller.signal,
    },
    '设备候选必须带 storeGuid / 去空白关键字 / 分页，并透传 AbortSignal',
  )
  assertEqual(devicePage.items.length, 1, '没有有效注册 ID 的设备行必须被丢弃')
  assertEqual(devicePage.items[0]?.systemDeviceNumber, 'POS_1002_0001', '设备候选应保留设备编号')
  assertEqual(devicePage.items[0]?.isOnline, true, '设备候选应保留在线状态')
  assertEqual(devicePage.total, 31, '设备候选应保留总数')
  assertEqual(devicePage.page, 2, '设备候选应保留页码')

  await service.getPosIpadOtaTargetDevices()
  assertDeepEqual(
    calls[calls.length - 1]?.params,
    { page: 1, pageSize: 20 },
    '未指定分店与关键字时不得传空条件，默认第 1 页 20 条',
  )

  const createdAssignment = await service.createPosIpadOtaAssignment({
    targetType: 'device',
    storeGuid: null,
    deviceRegistrationId: 42,
    releaseId: 'ota-1',
    forceUpdate: false,
    releaseMessage: null,
    note: '试点',
    expiresAtUtc: '2026-12-01T00:00:00.000Z',
    enabled: true,
  })
  assertEqual(calls[calls.length - 1]?.method, 'post', 'OTA 覆盖创建必须用 POST')
  assertEqual(calls[calls.length - 1]?.url, '/api/pos-ipad/ota-assignments', 'OTA 覆盖创建路径应固定')
  assertEqual(
    (calls[calls.length - 1]?.payload as { deviceRegistrationId?: number }).deviceRegistrationId,
    42,
    'OTA 覆盖创建必须透传设备注册 ID',
  )
  assertEqual(createdAssignment.deviceRegistrationId, 42, '创建结果应归一化')

  const updatedAssignment = await service.updatePosIpadOtaAssignment('assign-store', {
    expectedPolicyVersion: 7,
    releaseId: 'ota-2',
    forceUpdate: true,
    releaseMessage: null,
    note: null,
    expiresAtUtc: null,
    enabled: true,
  })
  assertDeepEqual(
    calls[calls.length - 1],
    {
      method: 'put',
      url: '/api/pos-ipad/ota-assignments/assign-store',
      payload: {
        expectedPolicyVersion: 7,
        releaseId: 'ota-2',
        forceUpdate: true,
        releaseMessage: null,
        note: null,
        expiresAtUtc: null,
        enabled: true,
      },
    },
    'OTA 覆盖修改必须 PUT 到 /{id} 并携带 expectedPolicyVersion',
  )
  assertEqual(updatedAssignment.policyVersion, 8, '修改结果应回填新的策略版本')

  await service.deletePosIpadOtaAssignment('assign/with space', 8, controller.signal)
  assertDeepEqual(
    calls[calls.length - 1],
    {
      method: 'delete',
      url: '/api/pos-ipad/ota-assignments/assign%2Fwith%20space',
      params: { expectedPolicyVersion: 8 },
      signal: controller.signal,
    },
    'OTA 覆盖删除必须 DELETE 并通过 query 带 expectedPolicyVersion，id 需转义',
  )

  const failingDeleteService = createAppUpdatePolicyService({
    ...transport,
    async delete() {
      return { success: false, errorCode: 'OTA_ASSIGNMENT_SAVE_FAILED', message: '删除失败' }
    },
  })
  let deleteRejected = false
  try {
    await failingDeleteService.deletePosIpadOtaAssignment('assign-store', 8)
  } catch (error) {
    deleteRejected = String(error).includes('OTA_ASSIGNMENT_SAVE_FAILED')
  }
  assertEqual(deleteRejected, true, 'HTTP 200 的业务失败删除响应必须抛错，不能误报删除成功')

  const emptyAssignmentService = createAppUpdatePolicyService({
    ...transport,
    async get() {
      return { success: true, data: null }
    },
  })
  assertDeepEqual(
    await emptyAssignmentService.getPosIpadOtaAssignments(),
    [],
    '覆盖列表 data 非数组时回退为空数组',
  )
  assertDeepEqual(
    (await emptyAssignmentService.getPosIpadOtaTargetDevices()).items,
    [],
    '设备候选 data 缺失时回退为空列表',
  )

  const failingService = createAppUpdatePolicyService({
    ...transport,
    async get() {
      return {
        success: false,
        errorCode: 'POLICY_LOAD_FAILED',
        message: '读取失败',
      }
    },
  })
  let rejected = false
  try {
    await failingService.getMobileIosNativePolicy()
  } catch (error) {
    rejected = String(error).includes('POLICY_LOAD_FAILED')
  }
  assertEqual(rejected, true, 'HTTP 200 的业务失败 ApiResponse 必须抛错')

  console.log('appUpdatePolicyService.test.ts: ok')
}

void run()
