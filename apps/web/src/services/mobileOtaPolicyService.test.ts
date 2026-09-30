import {
  createMobileOtaPolicyService,
  type MobileOtaPolicyTransport,
} from './mobileOtaPolicyService'

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
  method: 'get' | 'put'
  url: string
  payload?: unknown
  params?: Record<string, unknown>
  signal?: AbortSignal
}

const calls: TransportCall[] = []
const transport: MobileOtaPolicyTransport = {
  async get(url, options) {
    calls.push({ method: 'get', url, params: options?.params, signal: options?.signal })
    if (url === '/api/app-ota-releases') {
      return {
        success: true,
        data: [{
          id: 'release-1',
          releaseBatchId: 'batch-1',
          appKey: 'mobile',
          environment: 'production',
          clientChannel: 'production',
          releaseChannel: 'mobile-production-ios-release-1',
          easBranch: 'mobile-production-ios-release-1',
          projectName: 'hbgroup',
          platform: 'iOS',
          runtimeVersion: '1.0.2',
          updateGroupId: '11111111-1111-1111-1111-111111111111',
          updateId: '22222222-2222-2222-2222-222222222222',
          message: 'iOS OTA',
          gitCommitHash: 'abcdef12',
          dashboardUrl: 'https://expo.dev/update/release-1',
          publishedAtUtc: '2026-08-27T02:00:00Z',
          isRollback: false,
          factFingerprint: 'f'.repeat(64),
          legacy: false,
          registrationSource: 'mobile-release-script',
          createdAt: '2026-08-27T02:01:00Z',
          createdBy: 'release-bot',
        }, {
          id: 'cross-app-release',
          appKey: 'pos-handheld',
          environment: 'preview',
          clientChannel: 'preview',
          platform: 'ios',
          runtimeVersion: '1.0.2',
          updateId: 'cross-app-update',
        }],
      }
    }
    if (url.endsWith('/revisions')) {
      return {
        success: true,
        data: [{
          id: 3,
          environment: 'production',
          platform: 'ios',
          policyVersion: 2,
          action: 'save',
          snapshot: { enabled: true, required: false, targetReleaseId: 'release-1' },
          createdAt: '2026-08-27T03:00:00Z',
          createdBy: 'admin',
        }],
      }
    }
    return {
      success: true,
      data: {
        id: 'policy-1',
        environment: 'production',
        platform: 'iOS',
        enabled: true,
        required: false,
        policyVersion: 2,
        targetReleaseId: 'release-1',
        targetRuntimeVersion: '1.0.2',
        releaseMessage: '可选更新',
        targetRelease: null,
        additionalTargets: [
          { targetReleaseId: 'release-106', targetRuntimeVersion: '1.0.6' },
          { targetRuntimeVersion: '1.0.7' },
          'broken',
        ],
        updatedAt: '2026-08-27T03:00:00Z',
        updatedBy: 'admin',
      },
    }
  },
  async put(url, payload, options) {
    calls.push({ method: 'put', url, payload, signal: options?.signal })
    return {
      success: true,
      data: {
        id: 'policy-1',
        environment: 'production',
        platform: 'ios',
        enabled: true,
        required: true,
        policyVersion: 3,
        targetReleaseId: 'release-1',
        targetRuntimeVersion: '1.0.2',
        releaseMessage: '必须更新',
        targetRelease: null,
        updatedAt: '2026-08-27T04:00:00Z',
        updatedBy: 'admin',
      },
    }
  },
}

async function run() {
  const service = createMobileOtaPolicyService(transport)
  const controller = new AbortController()

  const releases = await service.getReleases('production', 'ios', controller.signal)
  assertEqual(releases.length, 1, 'Web 必须 fail closed 丢弃跨 app/environment/platform 的异常候选')
  assertEqual(releases[0]?.platform, 'ios', '发布事实平台必须规范化为小写 lane 值')
  assertDeepEqual(
    calls[calls.length - 1],
    {
      method: 'get',
      url: '/api/app-ota-releases',
      params: { appKey: 'mobile', environment: 'production', platform: 'ios' },
      signal: controller.signal,
    },
    '发布事实必须按 mobile/environment/platform 精确隔离并透传 AbortSignal',
  )

  const policy = await service.getPolicy('production', 'ios')
  assertEqual(policy.targetRuntimeVersion, '1.0.2', '策略必须保留服务端权威目标 Runtime')
  assertDeepEqual(
    policy.additionalTargets,
    [{ targetReleaseId: 'release-106', targetRuntimeVersion: '1.0.6' }],
    '策略必须保留按 Runtime 的附加目标，并丢弃缺少发布 ID 的异常项',
  )
  assertEqual(
    calls[calls.length - 1]?.url,
    '/api/app-update-policies/mobile-ota/production/ios',
    '策略读取路径必须锁定精确 lane',
  )

  const payload = {
    expectedPolicyVersion: 2,
    enabled: true,
    required: true,
    targetReleaseId: 'release-1',
    releaseMessage: '必须更新',
  }
  const saved = await service.savePolicy('production', 'ios', payload)
  assertEqual(saved.policyVersion, 3, '保存后必须采用服务端权威 policyVersion')
  assertDeepEqual(
    calls[calls.length - 1],
    {
      method: 'put',
      url: '/api/app-update-policies/mobile-ota/production/ios',
      payload,
      signal: undefined,
    },
    '保存必须把完整 CAS payload 写入精确 lane',
  )

  const revisions = await service.getRevisions('production', 'ios')
  assertEqual(revisions[0]?.policyVersion, 2, 'revision 必须保留策略版本')
  assertEqual(
    revisions[0]?.snapshotJson,
    JSON.stringify({ enabled: true, required: false, targetReleaseId: 'release-1' }),
    'revision 必须保留完整快照',
  )

  console.log('mobileOtaPolicyService.test.ts: ok')
}

void run()
