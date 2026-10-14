// 人脸比对接口响应的防御式规范化：字段缺失 / 类型漂移时界面不崩，且不放大授权（缺少权限标志一律当 false）。
// apiClient 的响应拦截器已把 ApiResponse<T> 解包成 T；这里只关心 T 的形状。
import { DEFAULT_UNWELCOME_AUTO_CONFIRM_SCORE } from "./constants";
import type {
  FaceCamera,
  FaceCameraList,
  FaceGateFacet,
  FaceGateReport,
  FaceMatchConfig,
  FaceMatchPage,
  FaceStoreProfile,
  FaceUnwelcomeBand,
  FaceUnwelcomeEntry,
  FaceUnwelcomeTask,
} from "./types";

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" ? (value as Record<string, unknown>) : {};
}

function asString(value: unknown, fallback = ""): string {
  return typeof value === "string" ? value : fallback;
}

function asNullableString(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value : null;
}

function asNumber(value: unknown, fallback = 0): number {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}

function asNullableNumber(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

function asArray<T>(value: unknown, map: (item: unknown) => T): T[] {
  return Array.isArray(value) ? value.map(map) : [];
}

function asStringArray(value: unknown): string[] {
  return Array.isArray(value)
    ? value.filter((item): item is string => typeof item === "string" && item.trim() !== "")
    : [];
}

export function normalizeFaceMatchConfig(value: unknown): FaceMatchConfig {
  const raw = asRecord(value);
  const permissions = asRecord(raw.permissions);
  return {
    enabled: raw.enabled === true,
    disabledReason: asNullableString(raw.disabledReason),
    region: asNullableString(raw.region),
    allowedRegions: asStringArray(raw.allowedRegions),
    objectStoreConfigured: raw.objectStoreConfigured === true,
    highBandScore: asNumber(raw.highBandScore),
    captureRetentionDays: asNumber(raw.captureRetentionDays),
    permissions: {
      manage: permissions.manage === true,
      trafficView: permissions.trafficView === true,
      unwelcomeManage: permissions.unwelcomeManage === true,
      unwelcomeApprove: permissions.unwelcomeApprove === true,
      unwelcomeReview: permissions.unwelcomeReview === true,
    },
  };
}

function normalizeGateFacet(value: unknown): FaceGateFacet {
  const raw = asRecord(value);
  return {
    facet: asString(raw.facet),
    requested: raw.requested === true,
    missing: asStringArray(raw.missing),
    warnings: asStringArray(raw.warnings),
  };
}

export function normalizeFaceGateReport(value: unknown): FaceGateReport {
  const raw = asRecord(value);
  const facets = asArray(raw.facets, normalizeGateFacet);
  // 始终以实际警告条目为准，避免标志位与列表不一致。
  const hasWarnings = raw.hasWarnings === true || facets.some((facet) => facet.warnings.length > 0);
  return { facets, canSave: raw.canSave === true, hasWarnings };
}

export function normalizeFaceStoreProfile(value: unknown): FaceStoreProfile {
  const raw = asRecord(value);
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
    // 0 是合法值（关闭自动确认），不能用 || 兜底；后端未返回（老版本 / 没有档案行）时取默认 90。
    unwelcomeAutoConfirmScore: asNumber(raw.unwelcomeAutoConfirmScore, DEFAULT_UNWELCOME_AUTO_CONFIRM_SCORE),
    updatedAtUtc: asNullableString(raw.updatedAtUtc),
    updatedBy: asNullableString(raw.updatedBy),
    effectiveTimeZoneId: asString(raw.effectiveTimeZoneId),
    jurisdiction: asNullableString(raw.jurisdiction),
    crossBorderReviewValid: raw.crossBorderReviewValid === true,
    gates: normalizeFaceGateReport(raw.gates),
  };
}

function normalizePage<T>(value: unknown, map: (item: unknown) => T): FaceMatchPage<T> {
  const raw = asRecord(value);
  return {
    items: asArray(raw.items, map),
    page: asNumber(raw.page) || 1,
    pageSize: asNumber(raw.pageSize) || 20,
    hasMore: raw.hasMore === true,
  };
}

export function normalizeFaceUnwelcomeEntry(value: unknown): FaceUnwelcomeEntry {
  const raw = asRecord(value);
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
    // 能否批准只认服务端的 canApprove（含提交人 ≠ 批准人、管理员可自批等规则），App 不自己推断。
    canApprove: raw.canApprove === true,
    cleanupPending: raw.cleanupPending === true,
  };
}

/** 档位只认 high；未知值一律当「一般」（页面不自己算阈值，也不把未知档位抬高）。 */
function asBand(value: unknown): FaceUnwelcomeBand {
  return value === "high" ? "high" : "normal";
}

export function normalizeFaceUnwelcomeTask(value: unknown): FaceUnwelcomeTask {
  const raw = asRecord(value);
  const score = asNumber(raw.score);
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
    nextStep: asString(raw.nextStep, "none"),
    // 能否复核只认服务端的 canReview，App 不自己推断。
    // nextStep 原样保留（first / none，旧后端可能还有 second），由 review-logic 统一折算成「待复核 / 已结束」。
    canReview: raw.canReview === true,
  };
}

export function normalizeFaceUnwelcomeEntryPage(value: unknown): FaceMatchPage<FaceUnwelcomeEntry> {
  return normalizePage(value, normalizeFaceUnwelcomeEntry);
}

export function normalizeFaceUnwelcomeTaskPage(value: unknown): FaceMatchPage<FaceUnwelcomeTask> {
  return normalizePage(value, normalizeFaceUnwelcomeTask);
}

export function normalizeFaceCamera(value: unknown): FaceCamera {
  const raw = asRecord(value);
  return {
    cameraId: asString(raw.cameraId),
    storeCode: asString(raw.storeCode),
    storeName: asNullableString(raw.storeName),
    deviceModel: asNullableString(raw.deviceModel),
    macAddress: asNullableString(raw.macAddress),
    note: asNullableString(raw.note),
    enabled: raw.enabled === true,
    createdAtUtc: asString(raw.createdAtUtc),
    updatedAtUtc: asString(raw.updatedAtUtc),
    lastSeenAtUtc: asNullableString(raw.lastSeenAtUtc),
    online: raw.online === true,
    captureCount24h: Math.max(0, asNumber(raw.captureCount24h)),
    faceCount24h: Math.max(0, asNumber(raw.faceCount24h)),
    parseFailureCount24h: Math.max(0, asNumber(raw.parseFailureCount24h)),
    lastEventAtUtc: asNullableString(raw.lastEventAtUtc),
  };
}

/**
 * 摄像头列表。刻意不读取、不保留 token / cameraUrl 等字段：
 * 列表接口本来就不返回它们，这里再保证即使服务端以后误带也不会流进页面 state。
 */
export function normalizeFaceCameraList(value: unknown): FaceCameraList {
  const raw = asRecord(value);
  return {
    items: asArray(raw.items, normalizeFaceCamera).filter((camera) => camera.cameraId),
    generatedAtUtc: asString(raw.generatedAtUtc),
    onlineWindowMinutes: asNumber(raw.onlineWindowMinutes) || 10,
    statsWindowHours: asNumber(raw.statsWindowHours) || 24,
  };
}
