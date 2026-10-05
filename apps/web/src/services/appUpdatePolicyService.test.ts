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
  method: 'get' | 'post' | 'put'
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
  async put(url, payload, options) {
    calls.push({ method: 'put', url, payload, signal: options?.signal })
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
