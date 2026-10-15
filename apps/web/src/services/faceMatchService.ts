import type { ApiResponse } from '../types/api'
import type {
  CreateFaceConsentInput,
  CreateFaceEnrollmentInput,
  CreateFaceUnwelcomeEntryInput,
  FaceAuditItem,
  FaceCaptureResult,
  FaceCaptureResultHit,
  FaceCaptureResultQuery,
  FaceConsent,
  FaceConsentWithdrawResult,
  FaceEnrollment,
  FaceGateFacet,
  FaceGateReport,
  FaceHitDetail,
  FaceHitRecipient,
  FaceMatchConfig,
  FaceMatchPage,
  FaceMatchPermissionFlags,
  FaceReviewVerdict,
  FaceStaffCandidate,
  FaceStoreProfile,
  FaceStoreProfileListItem,
  FaceStoreProfileUpdateRequest,
  FaceTraffic,
  FaceTrafficCounts,
  FaceUnwelcomeBand,
  FaceUnwelcomeEntry,
  FaceUnwelcomeStats,
  FaceUnwelcomeStatsBucket,
  FaceUnwelcomeStatsEntry,
  FaceUnwelcomeTask,
} from '../types/faceMatch'
import request, { RequestError, unwrapApiData } from '../utils/request'

// 门店人脸比对服务管理接口。返回 ApiResponse 信封，字段 camelCase；权限见各函数注释（Admin 靠角色放行）。
const API_BASE = '/api/react/v1/face-match'

/**
 * 安全约定：
 * 1. 接口响应不含对象键、COS 地址、腾讯云凭据、PersonId、FaceId；图片只能经专用内容接口由服务端转发字节。
 * 2. 请求统一带 cache: 'no-store'，不让浏览器 HTTP 缓存保留含生物识别信息的响应（服务端同样返回 no-store）。
 * 3. 抛出的错误不得带上传文件内容、图片字节或响应体里的敏感内容；本文件禁止出现 console.*，由 sourceContract 测试静态守住。
 */
const NO_STORE = { cache: 'no-store' } as const

/** 命中任务自动确认阈值的默认值（与后端一致）：档案行不存在、字段缺失或不是数字时使用。 */
export const DEFAULT_UNWELCOME_AUTO_CONFIRM_SCORE = 90
const API_BASE_URL = (((import.meta as ImportMeta & { env?: ImportMetaEnv }).env?.VITE_API_BASE_URL) || '').trim()

// ───────────── 归一化小工具（后端字段缺失 / 类型漂移时界面不崩） ─────────────

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' ? (value as Record<string, unknown>) : {}
}

function asString(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback
}

function asNullableString(value: unknown): string | null {
  return typeof value === 'string' && value.trim() ? value : null
}

function asNumber(value: unknown, fallback = 0): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback
}

function asNullableNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function asArray<T>(value: unknown, map: (item: unknown) => T): T[] {
  return Array.isArray(value) ? value.map(map) : []
}

function asStringArray(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string' && item.trim() !== '') : []
}

// ───────────── 归一化 ─────────────

export function normalizeFaceMatchConfig(value: unknown): FaceMatchConfig {
  const raw = asRecord(value)
  const permissions = asRecord(raw.permissions)
  const flags: FaceMatchPermissionFlags = {
    manage: permissions.manage === true,
    trafficView: permissions.trafficView === true,
    unwelcomeManage: permissions.unwelcomeManage === true,
    unwelcomeApprove: permissions.unwelcomeApprove === true,
    unwelcomeReview: permissions.unwelcomeReview === true,
    // 老后端没有这个字段：按没有「抓拍与比对」权限处理。
    resultsView: permissions.resultsView === true,
  }
  return {
    enabled: raw.enabled === true,
    disabledReason: asNullableString(raw.disabledReason),
    region: asNullableString(raw.region),
    allowedRegions: asStringArray(raw.allowedRegions),
    objectStoreConfigured: raw.objectStoreConfigured === true,
    highBandScore: asNumber(raw.highBandScore),
    captureRetentionDays: asNumber(raw.captureRetentionDays),
    permissions: flags,
  }
}

function normalizeGateFacet(value: unknown): FaceGateFacet {
  const raw = asRecord(value)
  // warnings / hasWarnings 是 2026-10-13 新增字段：老后端没有，缺省为空数组 / false，页面不崩。
  return {
    facet: asString(raw.facet),
    requested: raw.requested === true,
    missing: asStringArray(raw.missing),
    warnings: asStringArray(raw.warnings),
  }
}

export function normalizeFaceGateReport(value: unknown): FaceGateReport {
  const raw = asRecord(value)
  const facets = asArray(raw.facets, normalizeGateFacet)
  // 报告没带 hasWarnings 时按各 facet 的 warnings 推断（始终以实际警告条目为准，避免标志位与列表不一致）。
  const hasWarnings = raw.hasWarnings === true || facets.some((facet) => facet.warnings.length > 0)
  return { facets, canSave: raw.canSave === true, hasWarnings }
}

export function normalizeFaceStoreProfile(value: unknown): FaceStoreProfile {
  const raw = asRecord(value)
  return {
    storeCode: asString(raw.storeCode),
    storeName: asNullableString(raw.storeName),
    exists: raw.exists === true,
    mode: asNumber(raw.mode),
    evaluateMatch: raw.evaluateMatch === true,
    staffExclusionEnabled: raw.staffExclusionEnabled === true,
    trafficDedupEnabled: raw.trafficDedupEnabled === true,
    unwelcomeListEnabled: raw.unwelcomeListEnabled === true,
    allowedSubjects: asNullableString(raw.allowedSubjects),
    piaReference: asNullableString(raw.piaReference),
    signageInstalledOn: asNullableString(raw.signageInstalledOn),
    staffNoticeDate: asNullableString(raw.staffNoticeDate),
    legalSignoffBy: asNullableString(raw.legalSignoffBy),
    legalSignoffAt: asNullableString(raw.legalSignoffAt),
    dedupLegalSignoffBy: asNullableString(raw.dedupLegalSignoffBy),
    dedupLegalSignoffAt: asNullableString(raw.dedupLegalSignoffAt),
    crossBorderRegion: asNullableString(raw.crossBorderRegion),
    crossBorderReviewRef: asNullableString(raw.crossBorderReviewRef),
    crossBorderReviewedBy: asNullableString(raw.crossBorderReviewedBy),
    crossBorderReviewedAt: asNullableString(raw.crossBorderReviewedAt),
    timeZoneId: asNullableString(raw.timeZoneId),
    unwelcomeAssessmentRef: asNullableString(raw.unwelcomeAssessmentRef),
    unwelcomeLegalSignoffBy: asNullableString(raw.unwelcomeLegalSignoffBy),
    unwelcomeLegalSignoffAt: asNullableString(raw.unwelcomeLegalSignoffAt),
    staffTrainingDate: asNullableString(raw.staffTrainingDate),
    privacyContact: asNullableString(raw.privacyContact),
    // 生效值：老后端没有这个字段 / 不是数字时按默认 90（0 是合法值，表示不自动确认，不能被当成缺失）。
    unwelcomeAutoConfirmScore: asNumber(raw.unwelcomeAutoConfirmScore, DEFAULT_UNWELCOME_AUTO_CONFIRM_SCORE),
    updatedAtUtc: asNullableString(raw.updatedAtUtc),
    updatedBy: asNullableString(raw.updatedBy),
    effectiveTimeZoneId: asString(raw.effectiveTimeZoneId),
    jurisdiction: asNullableString(raw.jurisdiction),
    crossBorderReviewValid: raw.crossBorderReviewValid === true,
    gates: normalizeFaceGateReport(raw.gates),
  }
}

function normalizeProfileListItem(value: unknown): FaceStoreProfileListItem {
  const raw = asRecord(value)
  return {
    storeCode: asString(raw.storeCode),
    storeName: asNullableString(raw.storeName),
    mode: asNumber(raw.mode),
    evaluateMatch: raw.evaluateMatch === true,
    staffExclusionEnabled: raw.staffExclusionEnabled === true,
    trafficDedupEnabled: raw.trafficDedupEnabled === true,
    unwelcomeListEnabled: raw.unwelcomeListEnabled === true,
    crossBorderReviewValid: raw.crossBorderReviewValid === true,
    gateWarningCount: Math.max(0, Math.trunc(asNumber(raw.gateWarningCount))),
    updatedAtUtc: asNullableString(raw.updatedAtUtc),
  }
}

export function normalizeFaceStaffCandidate(value: unknown): FaceStaffCandidate {
  const raw = asRecord(value)
  return {
    userGuid: asString(raw.userGuid),
    displayName: asString(raw.displayName),
    email: asNullableString(raw.email),
    enrollmentState: asString(raw.enrollmentState, 'none'),
    requiresNswNotice: raw.requiresNswNotice === true,
  }
}

export function normalizeFaceConsent(value: unknown): FaceConsent {
  const raw = asRecord(value)
  return {
    consentId: asString(raw.consentId),
    userGuid: asString(raw.userGuid),
    userName: asNullableString(raw.userName),
    consentVersion: asString(raw.consentVersion),
    signedAtUtc: asString(raw.signedAtUtc),
    method: asString(raw.method),
    hasEvidence: raw.hasEvidence === true,
    nswNoticeDate: asNullableString(raw.nswNoticeDate),
    withdrawnAtUtc: asNullableString(raw.withdrawnAtUtc),
    withdrawnReason: asNullableString(raw.withdrawnReason),
    createdAtUtc: asString(raw.createdAtUtc),
    createdBy: asString(raw.createdBy),
    active: raw.active === true,
  }
}

export function normalizeFaceConsentWithdrawResult(value: unknown): FaceConsentWithdrawResult {
  const raw = asRecord(value)
  return {
    consent: normalizeFaceConsent(raw.consent),
    withdrawnEnrollmentCount: asNumber(raw.withdrawnEnrollmentCount),
    pendingRemoteDeletionCount: asNumber(raw.pendingRemoteDeletionCount),
  }
}

export function normalizeFaceEnrollment(value: unknown): FaceEnrollment {
  const raw = asRecord(value)
  return {
    enrollmentId: asString(raw.enrollmentId),
    userGuid: asString(raw.userGuid),
    userName: asNullableString(raw.userName),
    consentId: asString(raw.consentId),
    photoCount: asNumber(raw.photoCount),
    status: asNumber(raw.status),
    statusName: asString(raw.statusName),
    enrolledAtUtc: asString(raw.enrolledAtUtc),
    enrolledBy: asString(raw.enrolledBy),
    withdrawnAtUtc: asNullableString(raw.withdrawnAtUtc),
    pendingRemoteDeletion: raw.pendingRemoteDeletion === true,
  }
}

function normalizeTrafficCounts(value: unknown): FaceTrafficCounts {
  const raw = asRecord(value)
  return {
    captureCount: asNumber(raw.captureCount),
    staffMatchCount: asNumber(raw.staffMatchCount),
    uniqueVisitorCount: asNumber(raw.uniqueVisitorCount),
    repeatCount: asNumber(raw.repeatCount),
    lowQualityCount: asNumber(raw.lowQualityCount),
    apiFailureCount: asNumber(raw.apiFailureCount),
  }
}

export function normalizeFaceTraffic(value: unknown): FaceTraffic {
  const raw = asRecord(value)
  return {
    storeCode: asString(raw.storeCode),
    storeName: asNullableString(raw.storeName),
    timeZoneId: asString(raw.timeZoneId),
    from: asString(raw.from),
    to: asString(raw.to),
    days: asArray(raw.days, (item) => {
      const day = asRecord(item)
      return { date: asString(day.date), counts: normalizeTrafficCounts(day.counts) }
    }),
    hours: asArray(raw.hours, (item) => {
      const hour = asRecord(item)
      return { hour: asNumber(hour.hour), counts: normalizeTrafficCounts(hour.counts) }
    }),
    total: normalizeTrafficCounts(raw.total),
    // 估算标注是硬要求：即使后端漏了字段也按「估算」处理。
    uniqueVisitorIsEstimate: raw.uniqueVisitorIsEstimate !== false,
  }
}

function normalizeAuditItem(value: unknown): FaceAuditItem {
  const raw = asRecord(value)
  return {
    auditId: asNumber(raw.auditId),
    atUtc: asString(raw.atUtc),
    actorId: asString(raw.actorId),
    actorName: asString(raw.actorName),
    action: asString(raw.action),
    storeCode: asNullableString(raw.storeCode),
    targetRef: asNullableString(raw.targetRef),
    result: asString(raw.result),
  }
}

function normalizePage<T>(value: unknown, map: (item: unknown) => T): FaceMatchPage<T> {
  const raw = asRecord(value)
  return {
    items: asArray(raw.items, map),
    page: asNumber(raw.page) || 1,
    pageSize: asNumber(raw.pageSize) || 20,
    hasMore: raw.hasMore === true,
  }
}

export function normalizeFaceUnwelcomeEntry(value: unknown): FaceUnwelcomeEntry {
  const raw = asRecord(value)
  return {
    entryId: asString(raw.entryId),
    incidentCategory: asNumber(raw.incidentCategory),
    incidentCategoryName: asString(raw.incidentCategoryName),
    categoryNote: asNullableString(raw.categoryNote),
    evidenceRef: asString(raw.evidenceRef),
    storeScope: asStringArray(raw.storeScope),
    submittedById: asString(raw.submittedById),
    submittedByName: asString(raw.submittedByName),
    submittedAtUtc: asString(raw.submittedAtUtc),
    approvedByName: asNullableString(raw.approvedByName),
    approvedAtUtc: asNullableString(raw.approvedAtUtc),
    secondApprovedByName: asNullableString(raw.secondApprovedByName),
    secondApprovedAtUtc: asNullableString(raw.secondApprovedAtUtc),
    approvalsNeeded: asNumber(raw.approvalsNeeded),
    approvalsDone: asNumber(raw.approvalsDone),
    reviewDueAtUtc: asString(raw.reviewDueAtUtc),
    expiresAtUtc: asString(raw.expiresAtUtc),
    status: asNumber(raw.status),
    statusName: asString(raw.statusName),
    photoCount: asNumber(raw.photoCount),
    disputeNote: asNullableString(raw.disputeNote),
    expiringSoon: raw.expiringSoon === true,
    daysToExpiry: asNumber(raw.daysToExpiry),
    canApprove: raw.canApprove === true,
    cleanupPending: raw.cleanupPending === true,
  }
}

/** 档位只认 high；未知值一律当「一般」（页面不自己算阈值，也不把未知档位抬高）。 */
function asBand(value: unknown): FaceUnwelcomeBand {
  return value === 'high' ? 'high' : 'normal'
}

/** 命中通知的四个字段（复核任务与抓拍结果的命中摘要共用）：缺失按「排队中、0 人」，计数不为负。 */
function normalizeNotifyFields(raw: Record<string, unknown>) {
  return {
    notifyState: asNullableString(raw.notifyState) ?? 'pending',
    notifiedAtUtc: asNullableString(raw.notifiedAtUtc),
    notifyRecipientCount: Math.max(0, Math.trunc(asNumber(raw.notifyRecipientCount))),
    notifyReadCount: Math.max(0, Math.trunc(asNumber(raw.notifyReadCount))),
  }
}

export function normalizeFaceUnwelcomeTask(value: unknown): FaceUnwelcomeTask {
  const raw = asRecord(value)
  const score = asNumber(raw.score)
  return {
    taskId: asString(raw.taskId),
    storeCode: asString(raw.storeCode),
    storeName: asNullableString(raw.storeName),
    cameraId: asString(raw.cameraId),
    capturedAtUtc: asString(raw.capturedAtUtc),
    entryId: asString(raw.entryId),
    score,
    // 百分比以后端的整数为准；缺失时由 score 四舍五入。
    scorePercent: asNullableNumber(raw.scorePercent) ?? Math.round(score),
    band: asBand(raw.band),
    reviewStatus: asNumber(raw.reviewStatus),
    reviewStatusName: asString(raw.reviewStatusName),
    firstReviewerName: asNullableString(raw.firstReviewerName),
    firstReviewedAtUtc: asNullableString(raw.firstReviewedAtUtc),
    secondReviewerName: asNullableString(raw.secondReviewerName),
    secondReviewedAtUtc: asNullableString(raw.secondReviewedAtUtc),
    imageAvailable: raw.imageAvailable === true,
    imageDeleteAtUtc: asString(raw.imageDeleteAtUtc),
    referencePhotoCount: Math.max(0, Math.trunc(asNumber(raw.referencePhotoCount))),
    nextStep: asString(raw.nextStep, 'none'),
    canReview: raw.canReview === true,
    // 老后端没有这个字段：按「人工复核的任务」处理。
    autoConfirmed: raw.autoConfirmed === true,
    ...normalizeNotifyFields(raw),
  }
}

/** 相似度百分比：不是有限数字时为 null（页面显示「—」），否则四舍五入并夹在 0-100。 */
function asPercent(value: unknown): number | null {
  const number = asNullableNumber(value)
  return number === null ? null : Math.min(100, Math.max(0, Math.round(number)))
}

function normalizeCaptureResultHit(value: unknown): FaceCaptureResultHit | null {
  // 没有命中：null / 缺失 / 不是对象；没有任务编号的命中无法定位，也当作没有。
  if (!value || typeof value !== 'object') return null
  const raw = asRecord(value)
  const taskId = asString(raw.taskId)
  if (!taskId) return null
  return {
    taskId,
    scorePercent: asPercent(raw.scorePercent) ?? 0,
    // 档位只认后端明确给出的值；当前接口不返回，页面不自己用阈值划档。
    band: raw.band === 'high' ? 'high' : raw.band === 'normal' ? 'normal' : null,
    reviewStatus: asNumber(raw.reviewStatus),
    reviewStatusName: asString(raw.reviewStatusName),
    imageAvailable: raw.imageAvailable === true,
    ...normalizeNotifyFields(raw),
  }
}

export function normalizeFaceCaptureResult(value: unknown): FaceCaptureResult {
  const raw = asRecord(value)
  const evaluated = raw.evaluated === true
  return {
    captureId: asString(raw.captureId),
    storeCode: asString(raw.storeCode),
    storeName: asNullableString(raw.storeName),
    cameraId: asString(raw.cameraId),
    capturedAtUtc: asString(raw.capturedAtUtc),
    faceCount: Math.max(0, Math.trunc(asNumber(raw.faceCount))),
    isDuplicate: raw.isDuplicate === true,
    evaluated,
    // 判定保留原始取值（未知取值由页面兜底展示）；未评估一律没有判定。
    decision: evaluated ? asNullableString(raw.decision) : null,
    staffScorePercent: asPercent(raw.staffScorePercent),
    visitScorePercent: asPercent(raw.visitScorePercent),
    faceImageId: asNullableString(raw.faceImageId),
    sceneImageId: asNullableString(raw.sceneImageId),
    hit: normalizeCaptureResultHit(raw.hit),
  }
}

/** 展示名缺失（后端没给或是空白）时的占位，与后端 FaceHitResultService.UnknownDisplayName 一致。 */
const UNKNOWN_RECIPIENT_NAME = '-'

function normalizeFaceHitRecipient(value: unknown): FaceHitRecipient {
  const raw = asRecord(value)
  const read = raw.read === true
  return {
    displayName: asNullableString(raw.displayName)?.trim() ?? UNKNOWN_RECIPIENT_NAME,
    read,
    // 已读时间只在已读时有意义；未读却带了时间的脏数据按没有时间处理。
    readAtUtc: read ? asNullableString(raw.readAtUtc) : null,
  }
}

/**
 * 命中详情归一化：缺字段按安全默认值——收件人缺失 / 不是数组为空、目标图张数非法为 0（不显示目标图）、未撤回。
 * 张数夹在读取函数允许的下标范围内（最多 MAX_REFERENCE_INDEX + 1 张），避免点到永远读不了的下标。
 */
export function normalizeFaceHitDetail(value: unknown): FaceHitDetail {
  const raw = asRecord(value)
  return {
    taskId: asString(raw.taskId),
    targetPhotoCount: Math.min(MAX_REFERENCE_INDEX + 1, Math.max(0, Math.trunc(asNumber(raw.targetPhotoCount)))),
    notificationRevoked: raw.notificationRevoked === true,
    // 不是对象的数组项（null、字符串等）直接丢弃，不当成「-」收件人显示。
    recipients: Array.isArray(raw.recipients)
      ? raw.recipients.filter((item) => item !== null && typeof item === 'object').map(normalizeFaceHitRecipient)
      : [],
  }
}

function normalizeStatsEntry(value: unknown): FaceUnwelcomeStatsEntry {
  const raw = asRecord(value)
  return {
    entryId: asString(raw.entryId),
    statusName: asString(raw.statusName),
    taskCount: asNumber(raw.taskCount),
    pendingCount: asNumber(raw.pendingCount),
    dismissedCount: asNumber(raw.dismissedCount),
    confirmedCount: asNumber(raw.confirmedCount),
    // 系统自动确认的任务单独计数（不再算进「人工确认」）；老后端没有这个字段，缺失或不是数字按 0。
    autoConfirmedCount: Math.max(0, asNumber(raw.autoConfirmedCount)),
    dismissRate: asNullableNumber(raw.dismissRate),
    suggestReview: raw.suggestReview === true,
  }
}

function normalizeStatsBucket(value: unknown): FaceUnwelcomeStatsBucket {
  const raw = asRecord(value)
  return {
    from: asNumber(raw.from),
    to: asNumber(raw.to),
    total: asNumber(raw.total),
    dismissedCount: asNumber(raw.dismissedCount),
    confirmedCount: asNumber(raw.confirmedCount),
    // 系统自动确认的任务单独计数（不再算进「人工确认」）；老后端没有这个字段，缺失或不是数字按 0。
    autoConfirmedCount: Math.max(0, asNumber(raw.autoConfirmedCount)),
  }
}

export function normalizeFaceUnwelcomeStats(value: unknown): FaceUnwelcomeStats {
  const raw = asRecord(value)
  return {
    taskCount: asNumber(raw.taskCount),
    pendingCount: asNumber(raw.pendingCount),
    dismissedCount: asNumber(raw.dismissedCount),
    confirmedCount: asNumber(raw.confirmedCount),
    // 系统自动确认的任务单独计数（不再算进「人工确认」）；老后端没有这个字段，缺失或不是数字按 0。
    autoConfirmedCount: Math.max(0, asNumber(raw.autoConfirmedCount)),
    dismissRate: asNullableNumber(raw.dismissRate),
    entries: asArray(raw.entries, normalizeStatsEntry),
    scoreBuckets: asArray(raw.scoreBuckets, normalizeStatsBucket),
  }
}

// ───────────── 请求 ─────────────

function profilePath(storeCode: string) {
  return `${API_BASE}/profiles/${encodeURIComponent(storeCode)}`
}

type Envelope = ApiResponse<unknown> | unknown

/** 配置与权限标志（任何持有 FaceMatch.* 权限的账号）。 */
export async function getFaceMatchConfig(signal?: AbortSignal): Promise<FaceMatchConfig> {
  const response = await request.get<Envelope>(`${API_BASE}/config`, { signal, ...NO_STORE })
  return normalizeFaceMatchConfig(unwrapApiData(response))
}

// ----- 门店档案（Manage） -----

export async function getFaceStoreProfiles(signal?: AbortSignal): Promise<FaceStoreProfileListItem[]> {
  const response = await request.get<Envelope>(`${API_BASE}/profiles`, { signal, ...NO_STORE })
  return asArray(unwrapApiData(response), normalizeProfileListItem)
}

export async function getFaceStoreProfile(storeCode: string, signal?: AbortSignal): Promise<FaceStoreProfile> {
  const response = await request.get<Envelope>(profilePath(storeCode), { signal, ...NO_STORE })
  return normalizeFaceStoreProfile(unwrapApiData(response))
}

/**
 * 档案请求体（PUT 与 POST .../check 共用）：unwelcomeAutoConfirmScore 始终出现在 JSON 里。
 * 后端把「省略」和 null 都当作「保持库里原值」，所以这里把缺失 / 非数字统一写成 null，绝不静默改写成别的数字；
 * 保存前页面一定先校验过阈值，真正保存时这里就是用户填的数字。
 */
export function buildProfileRequestBody(payload: FaceStoreProfileUpdateRequest): FaceStoreProfileUpdateRequest {
  const score = payload.unwelcomeAutoConfirmScore
  return { ...payload, unwelcomeAutoConfirmScore: typeof score === 'number' && Number.isFinite(score) ? score : null }
}

/** 只检查闸门，不保存：用于表单实时提示「缺哪一项」。 */
export async function checkFaceStoreProfile(
  storeCode: string,
  payload: FaceStoreProfileUpdateRequest,
  signal?: AbortSignal,
): Promise<FaceGateReport> {
  const response = await request.post<Envelope>(`${profilePath(storeCode)}/check`, buildProfileRequestBody(payload), { signal, ...NO_STORE })
  return normalizeFaceGateReport(unwrapApiData(response))
}

/** 保存失败时闸门未满足返回 409 FACE_MATCH_GATE_NOT_MET，details 为闸门报告（见 logic.getGateReportFromError）。 */
export async function updateFaceStoreProfile(
  storeCode: string,
  payload: FaceStoreProfileUpdateRequest,
): Promise<FaceStoreProfile> {
  const response = await request.put<Envelope>(profilePath(storeCode), buildProfileRequestBody(payload), NO_STORE)
  return normalizeFaceStoreProfile(unwrapApiData(response))
}

// ----- 员工同意与登记（Manage） -----

export async function getFaceStaff(storeCode: string, keyword: string, signal?: AbortSignal): Promise<FaceStaffCandidate[]> {
  const response = await request.get<Envelope>(`${API_BASE}/staff`, {
    params: { storeCode, keyword: keyword.trim() || undefined },
    signal,
    ...NO_STORE,
  })
  return asArray(unwrapApiData(response), normalizeFaceStaffCandidate)
}

export async function getFaceConsents(userGuid: string, includeWithdrawn: boolean, signal?: AbortSignal): Promise<FaceConsent[]> {
  const response = await request.get<Envelope>(`${API_BASE}/consents`, {
    params: { userGuid, includeWithdrawn },
    signal,
    ...NO_STORE,
  })
  return asArray(unwrapApiData(response), normalizeFaceConsent)
}

/** multipart：scan 为同意书扫描件（pdf/jpg/png，≤10MB，纸质必传）。 */
export function buildFaceConsentFormData(input: CreateFaceConsentInput): FormData {
  const form = new FormData()
  form.append('userGuid', input.userGuid)
  form.append('consentVersion', input.consentVersion)
  form.append('signedAtUtc', input.signedAtUtc)
  form.append('method', input.method)
  if (input.nswNoticeDate) form.append('nswNoticeDate', input.nswNoticeDate)
  if (input.scan) form.append('scan', input.scan, input.scan.name)
  return form
}

export async function createFaceConsent(input: CreateFaceConsentInput): Promise<FaceConsent> {
  const response = await request.post<Envelope>(`${API_BASE}/consents`, buildFaceConsentFormData(input), NO_STORE)
  return normalizeFaceConsent(unwrapApiData(response))
}

export async function withdrawFaceConsent(consentId: string, reason: string): Promise<FaceConsentWithdrawResult> {
  const response = await request.post<Envelope>(
    `${API_BASE}/consents/${encodeURIComponent(consentId)}/withdraw`,
    { reason },
    NO_STORE,
  )
  return normalizeFaceConsentWithdrawResult(unwrapApiData(response))
}

export async function getFaceEnrollments(userGuid: string, activeOnly: boolean, signal?: AbortSignal): Promise<FaceEnrollment[]> {
  const response = await request.get<Envelope>(`${API_BASE}/enrollments`, {
    params: { userGuid, activeOnly },
    signal,
    ...NO_STORE,
  })
  return asArray(unwrapApiData(response), normalizeFaceEnrollment)
}

/** multipart：photos 重复 3–5 次，每张 jpeg/png ≤5MB。 */
export function buildFaceEnrollmentFormData(input: CreateFaceEnrollmentInput): FormData {
  const form = new FormData()
  form.append('userGuid', input.userGuid)
  form.append('consentId', input.consentId)
  for (const photo of input.photos) form.append('photos', photo, photo.name)
  return form
}

export async function createFaceEnrollment(input: CreateFaceEnrollmentInput): Promise<FaceEnrollment> {
  const response = await request.post<Envelope>(`${API_BASE}/enrollments`, buildFaceEnrollmentFormData(input), NO_STORE)
  return normalizeFaceEnrollment(unwrapApiData(response))
}

export async function withdrawFaceEnrollment(enrollmentId: string, reason: string): Promise<FaceEnrollment> {
  const response = await request.post<Envelope>(
    `${API_BASE}/enrollments/${encodeURIComponent(enrollmentId)}/withdraw`,
    { reason },
    NO_STORE,
  )
  return normalizeFaceEnrollment(unwrapApiData(response))
}

// ----- 客流统计（Traffic.View）与审计（Manage） -----

/** from / to 为 yyyy-MM-dd；单日（from == to）时返回分小时明细。 */
export async function getFaceTraffic(store: string, from: string, to: string, signal?: AbortSignal): Promise<FaceTraffic> {
  const response = await request.get<Envelope>(`${API_BASE}/traffic`, { params: { store, from, to }, signal, ...NO_STORE })
  return normalizeFaceTraffic(unwrapApiData(response))
}

export interface FaceAuditQuery {
  store?: string
  action?: string
  from?: string
  to?: string
  page: number
  pageSize: number
}

export async function getFaceAudit(query: FaceAuditQuery, signal?: AbortSignal): Promise<FaceMatchPage<FaceAuditItem>> {
  const response = await request.get<Envelope>(`${API_BASE}/audit`, {
    params: { ...query, store: query.store || undefined, action: query.action?.trim() || undefined },
    signal,
    ...NO_STORE,
  })
  return normalizePage(unwrapApiData(response), normalizeAuditItem)
}

// ----- 不受欢迎人员名单：条目（Manage / Approve） -----

export interface FaceUnwelcomeListQuery {
  /** 状态数值：条目 0-3、任务 0-2；不传为全部。 */
  status?: number
  storeCode?: string
  page: number
  pageSize: number
}

function listParams(query: FaceUnwelcomeListQuery) {
  return {
    status: query.status === undefined ? undefined : String(query.status),
    storeCode: query.storeCode || undefined,
    page: query.page,
    pageSize: query.pageSize,
  }
}

export async function getUnwelcomeEntries(
  query: FaceUnwelcomeListQuery,
  signal?: AbortSignal,
): Promise<FaceMatchPage<FaceUnwelcomeEntry>> {
  const response = await request.get<Envelope>(`${API_BASE}/unwelcome/entries`, {
    params: listParams(query),
    signal,
    ...NO_STORE,
  })
  return normalizePage(unwrapApiData(response), normalizeFaceUnwelcomeEntry)
}

/** multipart：photos 1–5 张（jpeg/png ≤5MB）；storeScope 逗号分隔门店编码。 */
export function buildUnwelcomeEntryFormData(input: CreateFaceUnwelcomeEntryInput): FormData {
  const form = new FormData()
  form.append('incidentCategory', String(input.incidentCategory))
  if (input.categoryNote) form.append('categoryNote', input.categoryNote)
  form.append('evidenceRef', input.evidenceRef)
  form.append('storeScope', input.storeScope.join(','))
  form.append('expiresAtUtc', input.expiresAtUtc)
  for (const photo of input.photos) form.append('photos', photo, photo.name)
  return form
}

export async function createUnwelcomeEntry(input: CreateFaceUnwelcomeEntryInput): Promise<FaceUnwelcomeEntry> {
  const response = await request.post<Envelope>(`${API_BASE}/unwelcome/entries`, buildUnwelcomeEntryFormData(input), NO_STORE)
  return normalizeFaceUnwelcomeEntry(unwrapApiData(response))
}

function entryPath(entryId: string) {
  return `${API_BASE}/unwelcome/entries/${encodeURIComponent(entryId)}`
}

export async function approveUnwelcomeEntry(entryId: string): Promise<FaceUnwelcomeEntry> {
  const response = await request.post<Envelope>(`${entryPath(entryId)}/approve`, undefined, NO_STORE)
  return normalizeFaceUnwelcomeEntry(unwrapApiData(response))
}

export async function withdrawUnwelcomeEntry(entryId: string, reason: string): Promise<FaceUnwelcomeEntry> {
  const response = await request.post<Envelope>(`${entryPath(entryId)}/withdraw`, { reason }, NO_STORE)
  return normalizeFaceUnwelcomeEntry(unwrapApiData(response))
}

export async function setUnwelcomeDisputeNote(entryId: string, note: string): Promise<FaceUnwelcomeEntry> {
  const response = await request.post<Envelope>(`${entryPath(entryId)}/dispute-note`, { note }, NO_STORE)
  return normalizeFaceUnwelcomeEntry(unwrapApiData(response))
}

// ----- 不受欢迎人员名单：复核任务（Review）与误报统计 -----

export async function getUnwelcomeTasks(
  query: FaceUnwelcomeListQuery,
  signal?: AbortSignal,
): Promise<FaceMatchPage<FaceUnwelcomeTask>> {
  const response = await request.get<Envelope>(`${API_BASE}/unwelcome/tasks`, {
    params: listParams(query),
    signal,
    ...NO_STORE,
  })
  return normalizePage(unwrapApiData(response), normalizeFaceUnwelcomeTask)
}

/** 单人复核：一个有复核权限的人判断一次即结束。match = 是同一个人 → 已确认；notMatch = 不是 → 已驳回。能否复核由后端 canReview 决定。 */
export async function reviewUnwelcomeTask(taskId: string, verdict: FaceReviewVerdict): Promise<FaceUnwelcomeTask> {
  const response = await request.post<Envelope>(
    `${API_BASE}/unwelcome/tasks/${encodeURIComponent(taskId)}/review`,
    { verdict },
    NO_STORE,
  )
  return normalizeFaceUnwelcomeTask(unwrapApiData(response))
}

export async function getUnwelcomeStats(signal?: AbortSignal): Promise<FaceUnwelcomeStats> {
  const response = await request.get<Envelope>(`${API_BASE}/unwelcome/stats`, { signal, ...NO_STORE })
  return normalizeFaceUnwelcomeStats(unwrapApiData(response))
}

// ───────────── 复核图片（敏感生物识别信息）：二进制取图 ─────────────

// 路径里的编号发请求前先校验，避免 ".." 之类被浏览器当成路径穿越而指向别的接口（编号来自服务端数据，这是第二道防线）。
const IMAGE_PATH_TASK_ID = /^[A-Za-z0-9-]{1,64}$/
const IMAGE_CONTENT_TYPES = ['image/jpeg', 'image/png']
/** 参考照片最多 5 张（下标 0-4）；多留余量，真实上限以任务的 referencePhotoCount 为准。 */
const MAX_REFERENCE_INDEX = 9

/**
 * 按需读取一张图片字节。1. request 只解析 JSON / 文本会弄坏二进制，所以单独用 fetch（带登录 Cookie）；
 * 2. 成功时只认 image/jpeg、image/png，返回 Blob 由调用方转 objectURL；响应体绝不写入缓存、存储、日志；
 * 3. 失败时抛 RequestError（status + 统一信封 payload），由 imageLogic.classifyReviewImageError 按状态码 / 错误码分类。
 */
async function fetchImageBlob(path: string, signal?: AbortSignal): Promise<Blob> {
  const url = `${API_BASE_URL}${path}`.replace(/([^:]\/)\/+/g, '$1')
  const response = await fetch(url, {
    method: 'GET',
    credentials: 'include',
    headers: { Accept: IMAGE_CONTENT_TYPES.join(', ') },
    signal,
    ...NO_STORE,
  })
  const contentType = (response.headers.get('content-type') || '').toLowerCase()
  if (response.ok && IMAGE_CONTENT_TYPES.some((type) => contentType.startsWith(type))) {
    return await response.blob()
  }
  const payload: unknown = contentType.includes('json') ? await response.json().catch(() => null) : null
  const message = asString(asRecord(payload).message) || `请求失败 (${response.status})`
  throw new RequestError(message, response.ok ? 502 : response.status, payload)
}

function invalidImageTarget(): RequestError {
  return new RequestError('FACE_MATCH_IMAGE_GONE', 404, { success: false, errorCode: 'FACE_MATCH_IMAGE_GONE' })
}

/** 命中抓拍：GET /unwelcome/tasks/{id}/image（需 Review 权限，服务端写审计）。 */
export async function fetchUnwelcomeTaskImage(taskId: string, signal?: AbortSignal): Promise<Blob> {
  if (!IMAGE_PATH_TASK_ID.test(taskId)) throw invalidImageTarget()
  return fetchImageBlob(`${API_BASE}/unwelcome/tasks/${taskId}/image`, signal)
}

/** 条目参考照片：GET /unwelcome/tasks/{id}/reference/{index}（index 从 0 起，张数见任务的 referencePhotoCount）。 */
export async function fetchUnwelcomeReferenceImage(taskId: string, index: number, signal?: AbortSignal): Promise<Blob> {
  if (!IMAGE_PATH_TASK_ID.test(taskId) || !Number.isInteger(index) || index < 0 || index > MAX_REFERENCE_INDEX) {
    throw invalidImageTarget()
  }
  return fetchImageBlob(`${API_BASE}/unwelcome/tasks/${taskId}/reference/${index}`, signal)
}

// ───────────── 抓拍与比对结果 ─────────────

/** 后端单页上限 50。 */
export const RESULTS_PAGE_SIZE_MAX = 50

/**
 * 抓拍与比对结果列表（需要 FaceCapture.Images.View）。列表只有图片编号，没有任何图片字节。
 * 条件里的空串 / false 不带上；fromUtc 含、toUtc 不含。
 */
export async function getFaceCaptureResults(
  query: FaceCaptureResultQuery,
  signal?: AbortSignal,
): Promise<FaceMatchPage<FaceCaptureResult>> {
  const response = await request.get<Envelope>(`${API_BASE}/results`, {
    params: {
      storeCode: query.storeCode || undefined,
      cameraId: query.cameraId || undefined,
      decision: query.decision || undefined,
      hitOnly: query.hitOnly ? 'true' : undefined,
      fromUtc: query.fromUtc || undefined,
      toUtc: query.toUtc || undefined,
      page: query.page,
      pageSize: Math.min(RESULTS_PAGE_SIZE_MAX, Math.max(1, query.pageSize)),
    },
    signal,
    ...NO_STORE,
  })
  return normalizePage(unwrapApiData(response), normalizeFaceCaptureResult)
}

/**
 * 抓拍详情抽屉的补充信息：GET /results/hits/{taskId}/detail（需 FaceCapture.Images.View，门店范围同命中流）。
 * 名单目标图张数 + 命中通知的收件人姓名与已读状态；不含图片、不含账号标识，服务端不写审计。
 * 只在抽屉打开且这条抓拍有命中时调用一次；任务编号发请求前先校验，非法编号不发请求（按「任务不存在」处理）。
 */
export async function fetchFaceHitDetail(taskId: string, signal?: AbortSignal): Promise<FaceHitDetail> {
  if (!IMAGE_PATH_TASK_ID.test(taskId)) {
    throw new RequestError('FACE_MATCH_NOT_FOUND', 404, { success: false, errorCode: 'FACE_MATCH_NOT_FOUND' })
  }
  const response = await request.get<Envelope>(`${API_BASE}/results/hits/${taskId}/detail`, { signal, ...NO_STORE })
  return normalizeFaceHitDetail(unwrapApiData(response))
}

/**
 * 抓拍结果里的一张图片：GET /results/{captureId}/images/{imageId}（需 FaceCapture.Images.View，服务端每次读取都写审计）。
 * 只能由用户点「查看」或打开详情后按需调用；编号发请求前先校验，非法编号不发请求。
 */
export async function fetchFaceCaptureResultImage(captureId: string, imageId: string, signal?: AbortSignal): Promise<Blob> {
  if (!IMAGE_PATH_TASK_ID.test(captureId) || !IMAGE_PATH_TASK_ID.test(imageId)) throw invalidImageTarget()
  return fetchImageBlob(`${API_BASE}/results/${captureId}/images/${imageId}`, signal)
}
