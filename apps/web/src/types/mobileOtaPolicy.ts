export type MobileOtaEnvironment = 'production' | 'preview'
export type MobileOtaPlatform = 'android' | 'ios'
export type AppOtaAppKey = 'mobile' | 'pos-handheld'

export interface AppOtaRelease {
  id: string
  releaseBatchId: string
  appKey: AppOtaAppKey
  environment: MobileOtaEnvironment
  clientChannel: string
  releaseChannel: string
  easBranch: string
  projectName: string
  platform: MobileOtaPlatform
  runtimeVersion: string
  updateGroupId: string
  updateId: string
  message: string | null
  gitCommitHash: string | null
  dashboardUrl: string | null
  publishedAtUtc: string
  isRollback: boolean
  rollbackOfReleaseId: string | null
  factFingerprint: string
  legacy: boolean
  registrationSource: string | null
  createdAt: string
  createdBy: string | null
}

/** 按 Runtime 挂在策略上的附加目标：与主目标 Runtime 不同的客户端各自收到这里的发布。 */
export interface MobileOtaAdditionalTarget {
  targetReleaseId: string
  targetRuntimeVersion: string | null
}

export interface MobileOtaPolicy {
  id: string | null
  environment: MobileOtaEnvironment
  platform: MobileOtaPlatform
  enabled: boolean
  required: boolean
  policyVersion: number
  targetReleaseId: string | null
  targetRuntimeVersion: string | null
  releaseMessage: string | null
  targetRelease: AppOtaRelease | null
  additionalTargets: MobileOtaAdditionalTarget[]
  updatedAt: string | null
  updatedBy: string | null
}

export interface MobileOtaPolicyRequest {
  expectedPolicyVersion: number
  enabled: boolean
  required: boolean
  targetReleaseId: string | null
  releaseMessage: string | null
}

export interface MobileOtaPolicyRevision {
  id: string
  environment: MobileOtaEnvironment
  platform: MobileOtaPlatform
  policyVersion: number
  operation: string
  snapshotJson: string
  createdAt: string
  createdBy: string | null
}
