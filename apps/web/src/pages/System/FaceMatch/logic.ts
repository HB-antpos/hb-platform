import dayjs from 'dayjs'

import { normalizeFaceGateReport } from '../../../services/faceMatchService'
import type {
  CreateFaceConsentInput,
  CreateFaceUnwelcomeEntryInput,
  FaceConsentMethod,
  FaceGateReport,
  FaceMatchConfig,
  FaceMatchPermissionFlags,
  FaceStaffCandidate,
  FaceStoreProfile,
  FaceStoreProfileUpdateRequest,
  FaceUnwelcomeBand,
  FaceUnwelcomeTask,
} from '../../../types/faceMatch'
import { RequestError } from '../../../utils/request'

// 人脸比对页的纯逻辑：页签可见性、档案表单规范化、闸门缺项文案键、错误分类、上传校验、日期规则、
// 相似度与复核流程展示、误报统计换算。页面与测试共用；文案一律是 faceMatch.* 文案键，由页面 t() 翻译。

// ───────────── 上传与字段规则（与后端一致） ─────────────

export const MB = 1024 * 1024
/** 同意书扫描件：pdf / jpg / png，≤10MB。 */
export const CONSENT_SCAN_MAX_BYTES = 10 * MB
export const CONSENT_SCAN_TYPES = ['application/pdf', 'image/jpeg', 'image/png'] as const
/** 登记照 / 名单参考照片：jpeg / png，≤5MB。 */
export const PHOTO_MAX_BYTES = 5 * MB
export const PHOTO_TYPES = ['image/jpeg', 'image/png'] as const
export const ENROLLMENT_PHOTO_MIN = 3
export const ENROLLMENT_PHOTO_MAX = 5
export const UNWELCOME_PHOTO_MIN = 1
export const UNWELCOME_PHOTO_MAX = 5

/** NSW 门店员工：书面通知到签署同意至少间隔 14 天。 */
export const NSW_NOTICE_MIN_DAYS = 14
/** 名单条目到期日上限 12 个月。 */
export const UNWELCOME_MAX_EXPIRY_MONTHS = 12
export const CATEGORY_NOTE_MAX_LENGTH = 200
export const EVIDENCE_REF_MAX_LENGTH = 100
export const DISPUTE_NOTE_MAX_LENGTH = 500
export const REASON_MAX_LENGTH = 200
export const CONSENT_VERSION_MAX_LENGTH = 50
export const PAGE_SIZE = 20

/** 客流统计单次最多查询的天数。 */
export const TRAFFIC_MAX_RANGE_DAYS = 92

/** 一条校验结果：文案键 + 插值参数。 */
export interface ValidationIssue {
  key: string
  params?: Record<string, unknown>
}

/** 去首尾空白；空串（含全空白）规范成 null，后端把 null 当作「未填写」。 */
export function normalizeText(value: string | null | undefined): string | null {
  const trimmed = (value ?? '').trim()
  return trimmed ? trimmed : null
}

// ───────────── 页签可见性 ─────────────

export type FaceMatchTabKey = 'profiles' | 'staff' | 'traffic' | 'unwelcome' | 'review' | 'stats' | 'audit'

/**
 * 按后端 /config 返回的权限标志决定页签（顺序即页面顺序）。
 * 名单条目：Manage 或 Approve；复核任务：Review；误报统计：三个名单权限任一；审计：Manage。
 */
export function getVisibleTabs(permissions: FaceMatchPermissionFlags): FaceMatchTabKey[] {
  const tabs: FaceMatchTabKey[] = []
  if (permissions.manage) tabs.push('profiles', 'staff')
  if (permissions.trafficView) tabs.push('traffic')
  if (permissions.unwelcomeManage || permissions.unwelcomeApprove) tabs.push('unwelcome')
  if (permissions.unwelcomeReview) tabs.push('review')
  if (permissions.unwelcomeManage || permissions.unwelcomeApprove || permissions.unwelcomeReview) tabs.push('stats')
  if (permissions.manage) tabs.push('audit')
  return tabs
}

/** 功能未开启的原因文案键（文案键写成字面量映射：契约测试按字面量扫描键）。 */
export const DISABLED_REASON_KEYS: Record<string, string> = {
  'region-missing': 'faceMatch.disabled.reasons.regionMissing',
  'region-not-allowed': 'faceMatch.disabled.reasons.regionNotAllowed',
  'credentials-missing': 'faceMatch.disabled.reasons.credentialsMissing',
  'credentials-invalid': 'faceMatch.disabled.reasons.credentialsInvalid',
}

export const DISABLED_REASON_FALLBACK_KEY = 'faceMatch.disabled.reasons.unknown'

export function getDisabledReasonKey(config: Pick<FaceMatchConfig, 'disabledReason'>): string {
  return (config.disabledReason && DISABLED_REASON_KEYS[config.disabledReason]) || DISABLED_REASON_FALLBACK_KEY
}

// ───────────── 门店档案表单 ─────────────

export const PROFILE_MODE_OFF = 0
export const PROFILE_MODE_CAPTURE = 1
export const PROFILE_MODE_MATCH = 2

export const PROFILE_MODE_OPTIONS = [
  { value: PROFILE_MODE_OFF, labelKey: 'faceMatch.profile.mode.off', hintKey: 'faceMatch.profile.mode.offHint' },
  { value: PROFILE_MODE_CAPTURE, labelKey: 'faceMatch.profile.mode.capture', hintKey: 'faceMatch.profile.mode.captureHint' },
  { value: PROFILE_MODE_MATCH, labelKey: 'faceMatch.profile.mode.match', hintKey: 'faceMatch.profile.mode.matchHint' },
] as const

/** 表单值：文本一律是字符串（空串 = 未填写），日期是 yyyy-MM-dd，日期时间是 UTC ISO。 */
export interface FaceProfileFormValues {
  mode: number
  evaluateMatch: boolean
  staffExclusionEnabled: boolean
  trafficDedupEnabled: boolean
  unwelcomeListEnabled: boolean
  allowedSubjects: string
  piaReference: string
  signageInstalledOn: string
  staffNoticeDate: string
  legalSignoffBy: string
  legalSignoffAt: string
  dedupLegalSignoffBy: string
  dedupLegalSignoffAt: string
  crossBorderRegion: string
  crossBorderReviewRef: string
  crossBorderReviewedBy: string
  crossBorderReviewedAt: string
  timeZoneId: string
  unwelcomeAssessmentRef: string
  unwelcomeLegalSignoffBy: string
  unwelcomeLegalSignoffAt: string
  staffTrainingDate: string
  privacyContact: string
}

/**
 * 档案 → 表单初始值。跨境披露审查的地域为空时预填当前配置地域（页面据此提示「审查的是哪个地域」）；
 * 已保存但与当前地域不一致时保持原值，由页面提示「须重新审查」，不悄悄改写已有审查记录。
 */
export function profileToFormValues(
  profile: FaceStoreProfile,
  configRegion: string | null | undefined,
): FaceProfileFormValues {
  return {
    mode: profile.mode ?? PROFILE_MODE_OFF,
    evaluateMatch: profile.evaluateMatch,
    staffExclusionEnabled: profile.staffExclusionEnabled,
    trafficDedupEnabled: profile.trafficDedupEnabled,
    unwelcomeListEnabled: profile.unwelcomeListEnabled,
    allowedSubjects: profile.allowedSubjects ?? '',
    piaReference: profile.piaReference ?? '',
    signageInstalledOn: profile.signageInstalledOn ?? '',
    staffNoticeDate: profile.staffNoticeDate ?? '',
    legalSignoffBy: profile.legalSignoffBy ?? '',
    legalSignoffAt: profile.legalSignoffAt ?? '',
    dedupLegalSignoffBy: profile.dedupLegalSignoffBy ?? '',
    dedupLegalSignoffAt: profile.dedupLegalSignoffAt ?? '',
    crossBorderRegion: profile.crossBorderRegion ?? configRegion ?? '',
    crossBorderReviewRef: profile.crossBorderReviewRef ?? '',
    crossBorderReviewedBy: profile.crossBorderReviewedBy ?? '',
    crossBorderReviewedAt: profile.crossBorderReviewedAt ?? '',
    timeZoneId: profile.timeZoneId ?? '',
    unwelcomeAssessmentRef: profile.unwelcomeAssessmentRef ?? '',
    unwelcomeLegalSignoffBy: profile.unwelcomeLegalSignoffBy ?? '',
    unwelcomeLegalSignoffAt: profile.unwelcomeLegalSignoffAt ?? '',
    staffTrainingDate: profile.staffTrainingDate ?? '',
    privacyContact: profile.privacyContact ?? '',
  }
}

/** 表单值 → 请求体：去首尾空白，空串规范成 null（后端把 null 当作「未填写」）。 */
export function buildProfileRequest(values: Partial<FaceProfileFormValues>): FaceStoreProfileUpdateRequest {
  return {
    mode: typeof values.mode === 'number' ? values.mode : PROFILE_MODE_OFF,
    evaluateMatch: values.evaluateMatch === true,
    staffExclusionEnabled: values.staffExclusionEnabled === true,
    trafficDedupEnabled: values.trafficDedupEnabled === true,
    unwelcomeListEnabled: values.unwelcomeListEnabled === true,
    allowedSubjects: normalizeText(values.allowedSubjects),
    piaReference: normalizeText(values.piaReference),
    signageInstalledOn: normalizeText(values.signageInstalledOn),
    staffNoticeDate: normalizeText(values.staffNoticeDate),
    legalSignoffBy: normalizeText(values.legalSignoffBy),
    legalSignoffAt: normalizeText(values.legalSignoffAt),
    dedupLegalSignoffBy: normalizeText(values.dedupLegalSignoffBy),
    dedupLegalSignoffAt: normalizeText(values.dedupLegalSignoffAt),
    crossBorderRegion: normalizeText(values.crossBorderRegion),
    crossBorderReviewRef: normalizeText(values.crossBorderReviewRef),
    crossBorderReviewedBy: normalizeText(values.crossBorderReviewedBy),
    crossBorderReviewedAt: normalizeText(values.crossBorderReviewedAt),
    timeZoneId: normalizeText(values.timeZoneId),
    unwelcomeAssessmentRef: normalizeText(values.unwelcomeAssessmentRef),
    unwelcomeLegalSignoffBy: normalizeText(values.unwelcomeLegalSignoffBy),
    unwelcomeLegalSignoffAt: normalizeText(values.unwelcomeLegalSignoffAt),
    staffTrainingDate: normalizeText(values.staffTrainingDate),
    privacyContact: normalizeText(values.privacyContact),
  }
}

/** 两份请求体是否相同（用于「有未保存的修改」提示与避免重复检查）。 */
export function isSameProfileRequest(a: FaceStoreProfileUpdateRequest, b: FaceStoreProfileUpdateRequest): boolean {
  return JSON.stringify(a) === JSON.stringify(b)
}

// ───────────── 闸门缺项 ─────────────

/** 闸门单项（facet）名称。 */
export const FACET_LABEL_KEYS: Record<string, string> = {
  capture: 'faceMatch.gates.facets.capture',
  match: 'faceMatch.gates.facets.match',
  staffExclusion: 'faceMatch.gates.facets.staffExclusion',
  trafficDedup: 'faceMatch.gates.facets.trafficDedup',
  unwelcome: 'faceMatch.gates.facets.unwelcome',
}

/**
 * Missing 键 → 文案键。字段键（小驼峰，与请求体同名）直接使用表单字段名称；特殊键有各自的说明。
 * 键写成字面量映射而不是模板拼接：契约测试按字面量扫描键。
 */
export const MISSING_KEY_LABEL_KEYS: Record<string, string> = {
  mode: 'faceMatch.profile.fields.mode',
  allowedSubjects: 'faceMatch.profile.fields.allowedSubjects',
  piaReference: 'faceMatch.profile.fields.piaReference',
  signageInstalledOn: 'faceMatch.profile.fields.signageInstalledOn',
  staffNoticeDate: 'faceMatch.profile.fields.staffNoticeDate',
  legalSignoffBy: 'faceMatch.profile.fields.legalSignoffBy',
  legalSignoffAt: 'faceMatch.profile.fields.legalSignoffAt',
  dedupLegalSignoffBy: 'faceMatch.profile.fields.dedupLegalSignoffBy',
  dedupLegalSignoffAt: 'faceMatch.profile.fields.dedupLegalSignoffAt',
  crossBorderRegion: 'faceMatch.profile.fields.crossBorderRegion',
  crossBorderReviewRef: 'faceMatch.profile.fields.crossBorderReviewRef',
  crossBorderReviewedBy: 'faceMatch.profile.fields.crossBorderReviewedBy',
  crossBorderReviewedAt: 'faceMatch.profile.fields.crossBorderReviewedAt',
  timeZoneId: 'faceMatch.profile.fields.timeZoneId',
  unwelcomeAssessmentRef: 'faceMatch.profile.fields.unwelcomeAssessmentRef',
  unwelcomeLegalSignoffBy: 'faceMatch.profile.fields.unwelcomeLegalSignoffBy',
  unwelcomeLegalSignoffAt: 'faceMatch.profile.fields.unwelcomeLegalSignoffAt',
  staffTrainingDate: 'faceMatch.profile.fields.staffTrainingDate',
  privacyContact: 'faceMatch.profile.fields.privacyContact',
  activeEnrollment: 'faceMatch.gates.special.activeEnrollment',
  regionMismatch: 'faceMatch.gates.special.regionMismatch',
  staffNoticeTooRecent: 'faceMatch.gates.special.staffNoticeTooRecent',
  modeOff: 'faceMatch.gates.special.modeOff',
  evaluateMatchRequiresCapture: 'faceMatch.gates.special.evaluateMatchRequiresCapture',
}

/** 后端以后新增的缺项键没有登记时，界面显示键名本身（小驼峰字段名），不能崩也不能隐藏缺项。 */
export function getMissingLabelKey(missingKey: string): string | null {
  return MISSING_KEY_LABEL_KEYS[missingKey] ?? null
}

export interface GateRow {
  facet: string
  facetLabelKey: string | null
  missing: string[]
}

/** 只列出「被请求启用」的闸门项；Requested=false 的项不展示（没有要求也就谈不上缺项）。 */
export function summarizeGates(report: FaceGateReport | null | undefined): GateRow[] {
  if (!report) return []
  return report.facets
    .filter((facet) => facet.requested)
    .map((facet) => ({ facet: facet.facet, facetLabelKey: FACET_LABEL_KEYS[facet.facet] ?? null, missing: facet.missing }))
}

export function countMissing(rows: ReadonlyArray<GateRow>): number {
  return rows.reduce((sum, row) => sum + row.missing.length, 0)
}

// ───────────── 错误分类 ─────────────

/**
 * 页面已知的业务错误码（FACE_MATCH_*）→ 文案键。后端可以增加新码：不认识的码会按 HTTP 状态码给出通用说明，
 * 再不行走通用文案；后端消息文本不直接当作唯一文案展示。
 */
export const FACE_MATCH_ERROR_KEYS: Record<string, string> = {
  FACE_MATCH_DISABLED: 'faceMatch.errors.FACE_MATCH_DISABLED',
  FACE_MATCH_STORE_NOT_CONFIGURED: 'faceMatch.errors.FACE_MATCH_STORE_NOT_CONFIGURED',
  FACE_MATCH_FORBIDDEN: 'faceMatch.errors.FACE_MATCH_FORBIDDEN',
  FACE_MATCH_STORE_FORBIDDEN: 'faceMatch.errors.FACE_MATCH_STORE_FORBIDDEN',
  FACE_MATCH_STORE_INVALID: 'faceMatch.errors.FACE_MATCH_STORE_INVALID',
  FACE_MATCH_STORE_NOT_FOUND: 'faceMatch.errors.FACE_MATCH_STORE_NOT_FOUND',
  FACE_MATCH_FIELD_INVALID: 'faceMatch.errors.FACE_MATCH_FIELD_INVALID',
  FACE_MATCH_NOT_FOUND: 'faceMatch.errors.FACE_MATCH_NOT_FOUND',
  FACE_MATCH_GATE_NOT_MET: 'faceMatch.errors.FACE_MATCH_GATE_NOT_MET',
  FACE_MATCH_CONSENT_WITHDRAWN: 'faceMatch.errors.FACE_MATCH_CONSENT_WITHDRAWN',
  FACE_MATCH_NSW_NOTICE_REQUIRED: 'faceMatch.errors.FACE_MATCH_NSW_NOTICE_REQUIRED',
  FACE_MATCH_NSW_NOTICE_TOO_RECENT: 'faceMatch.errors.FACE_MATCH_NSW_NOTICE_TOO_RECENT',
  FACE_MATCH_ENROLLMENT_EXISTS: 'faceMatch.errors.FACE_MATCH_ENROLLMENT_EXISTS',
  FACE_MATCH_PHOTO_UNUSABLE: 'faceMatch.errors.FACE_MATCH_PHOTO_UNUSABLE',
  FACE_MATCH_WITHDRAW_PENDING: 'faceMatch.errors.FACE_MATCH_WITHDRAW_PENDING',
  FACE_MATCH_TENCENT_AUTH: 'faceMatch.errors.FACE_MATCH_TENCENT_AUTH',
  FACE_MATCH_TENCENT_BUSY: 'faceMatch.errors.FACE_MATCH_TENCENT_BUSY',
  FACE_MATCH_TENCENT_LIMIT: 'faceMatch.errors.FACE_MATCH_TENCENT_LIMIT',
  FACE_MATCH_TENCENT_CONFLICT: 'faceMatch.errors.FACE_MATCH_TENCENT_CONFLICT',
  FACE_MATCH_TENCENT_UNAVAILABLE: 'faceMatch.errors.FACE_MATCH_TENCENT_UNAVAILABLE',
  FACE_MATCH_TENCENT_ERROR: 'faceMatch.errors.FACE_MATCH_TENCENT_ERROR',
  FACE_MATCH_ENTRY_NOT_PENDING: 'faceMatch.errors.FACE_MATCH_ENTRY_NOT_PENDING',
  FACE_MATCH_APPROVER_IS_SUBMITTER: 'faceMatch.errors.FACE_MATCH_APPROVER_IS_SUBMITTER',
  FACE_MATCH_ALREADY_APPROVED_BY_YOU: 'faceMatch.errors.FACE_MATCH_ALREADY_APPROVED_BY_YOU',
  FACE_MATCH_ENTRY_EXPIRED: 'faceMatch.errors.FACE_MATCH_ENTRY_EXPIRED',
  FACE_MATCH_UNWELCOME_NOT_ENABLED: 'faceMatch.errors.FACE_MATCH_UNWELCOME_NOT_ENABLED',
  FACE_MATCH_ENTRY_CHANGED: 'faceMatch.errors.FACE_MATCH_ENTRY_CHANGED',
  FACE_MATCH_TASK_CLOSED: 'faceMatch.errors.FACE_MATCH_TASK_CLOSED',
  FACE_MATCH_SAME_REVIEWER: 'faceMatch.errors.FACE_MATCH_SAME_REVIEWER',
  FACE_MATCH_TASK_CHANGED: 'faceMatch.errors.FACE_MATCH_TASK_CHANGED',
  FACE_MATCH_IMAGE_GONE: 'faceMatch.errors.FACE_MATCH_IMAGE_GONE',
  FACE_MATCH_IMAGE_TOO_LARGE: 'faceMatch.errors.FACE_MATCH_IMAGE_TOO_LARGE',
  FACE_MATCH_IMAGE_UNSUPPORTED: 'faceMatch.errors.FACE_MATCH_IMAGE_UNSUPPORTED',
  FACE_MATCH_STORE_DENIED: 'faceMatch.errors.FACE_MATCH_STORE_DENIED',
  FACE_MATCH_STORE_TIMEOUT: 'faceMatch.errors.FACE_MATCH_STORE_TIMEOUT',
  FACE_MATCH_STORE_ERROR: 'faceMatch.errors.FACE_MATCH_STORE_ERROR',
  FACE_MATCH_AUDIT_FAILED: 'faceMatch.errors.FACE_MATCH_AUDIT_FAILED',
  FACE_MATCH_TIMEZONE_UNAVAILABLE: 'faceMatch.errors.FACE_MATCH_TIMEZONE_UNAVAILABLE',
}

/** 按 HTTP 状态码的兜底说明（错误码不认识时）。 */
export const STATUS_ERROR_KEYS: Record<number, string> = {
  401: 'faceMatch.errors.status401',
  403: 'faceMatch.errors.status403',
  404: 'faceMatch.errors.status404',
  409: 'faceMatch.errors.status409',
  413: 'faceMatch.errors.status413',
  503: 'faceMatch.errors.status503',
}

export const GENERIC_ERROR_KEY = 'faceMatch.errors.generic'

/** 从请求错误里取错误码；只读 errorCode / code，不读取、不返回响应体的其他内容。 */
export function getFaceMatchErrorCode(error: unknown): string | null {
  if (!(error instanceof RequestError)) return null
  const payload = error.payload
  if (!payload || typeof payload !== 'object') return null
  const raw = payload as Record<string, unknown>
  const code = raw.errorCode ?? raw.code
  return typeof code === 'string' && code ? code : null
}

/** 已知错误码（登记在 FACE_MATCH_ERROR_KEYS 里的）；不认识返回 null。 */
export function getKnownFaceMatchErrorCode(error: unknown): string | null {
  const code = getFaceMatchErrorCode(error)
  return code && code in FACE_MATCH_ERROR_KEYS ? code : null
}

/** 错误 → 文案键：已知错误码优先，其次按 HTTP 状态码，最后通用文案。 */
export function classifyFaceMatchError(error: unknown): string {
  const code = getKnownFaceMatchErrorCode(error)
  if (code) return FACE_MATCH_ERROR_KEYS[code]
  if (error instanceof RequestError && error.status in STATUS_ERROR_KEYS) return STATUS_ERROR_KEYS[error.status]
  return GENERIC_ERROR_KEY
}

function getErrorDetails(error: unknown): Record<string, unknown> | null {
  if (!(error instanceof RequestError)) return null
  const payload = error.payload
  const details = payload && typeof payload === 'object' ? (payload as Record<string, unknown>).details : null
  return details && typeof details === 'object' ? (details as Record<string, unknown>) : null
}

/** 字段名只接受普通标识符（后端 details.field），别的内容一律不展示。 */
const FIELD_NAME_PATTERN = /^[A-Za-z][A-Za-z0-9_.]{0,63}$/

/**
 * 错误 → 面向用户的文案（供各页签统一使用）：
 * - FACE_MATCH_DISABLED 的 details.reason 是已知取值时，追加具体原因（缺地域 / 缺凭据等）；
 * - FACE_MATCH_FIELD_INVALID 的 details.field 是普通字段名时，追加「字段：xxx」帮助定位；
 * 其余只用 classifyFaceMatchError 的结果，后端消息文本不直接展示。
 */
export function describeFaceMatchError(
  translate: (key: string, params?: Record<string, unknown>) => string,
  error: unknown,
): string {
  const base = translate(classifyFaceMatchError(error))
  const code = getKnownFaceMatchErrorCode(error)
  const details = getErrorDetails(error)
  if (code === 'FACE_MATCH_DISABLED') {
    const reason = details?.reason
    const reasonKey = typeof reason === 'string' ? DISABLED_REASON_KEYS[reason] : undefined
    return reasonKey ? `${base} ${translate(reasonKey)}` : base
  }
  if (code === 'FACE_MATCH_FIELD_INVALID') {
    const field = details?.field
    return typeof field === 'string' && FIELD_NAME_PATTERN.test(field) ? `${base}${translate('faceMatch.errors.fieldSuffix', { field })}` : base
  }
  return base
}

export function isForbiddenError(error: unknown) {
  return error instanceof RequestError && error.status === 403
}

/** 保存档案被闸门拒绝（409 FACE_MATCH_GATE_NOT_MET）时，取 details 里的闸门报告；否则 null。 */
export function getGateReportFromError(error: unknown): FaceGateReport | null {
  if (getFaceMatchErrorCode(error) !== 'FACE_MATCH_GATE_NOT_MET') return null
  const payload = (error as RequestError).payload
  const details = payload && typeof payload === 'object' ? (payload as Record<string, unknown>).details : null
  if (!details || typeof details !== 'object') return null
  return normalizeFaceGateReport(details)
}

// ───────────── 上传校验 ─────────────

export interface UploadFileLike {
  name: string
  type: string
  size: number
}

const EXTENSION_TYPES: Record<string, string> = {
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  png: 'image/png',
  pdf: 'application/pdf',
}

/** 文件类型：优先浏览器给的 MIME，缺失时按扩展名判断（部分系统拖拽进来的文件 type 为空）。 */
export function detectFileType(file: Pick<UploadFileLike, 'name' | 'type'>): string {
  const mime = (file.type || '').toLowerCase()
  if (mime) return mime === 'image/jpg' ? 'image/jpeg' : mime
  const extension = file.name.includes('.') ? file.name.split('.').pop()!.toLowerCase() : ''
  return EXTENSION_TYPES[extension] ?? ''
}

export interface UploadRule {
  types: ReadonlyArray<string>
  maxBytes: number
}

export const PHOTO_RULE: UploadRule = { types: PHOTO_TYPES, maxBytes: PHOTO_MAX_BYTES }
export const CONSENT_SCAN_RULE: UploadRule = { types: CONSENT_SCAN_TYPES, maxBytes: CONSENT_SCAN_MAX_BYTES }

/** 单个文件：类型 → 空文件 → 大小。 */
export function validateFile(file: UploadFileLike, rule: UploadRule): ValidationIssue | null {
  if (!rule.types.includes(detectFileType(file))) {
    return { key: 'faceMatch.validation.fileType', params: { types: rule.types.map(typeLabel).join(' / ') } }
  }
  if (file.size <= 0) return { key: 'faceMatch.validation.fileEmpty' }
  if (file.size > rule.maxBytes) return { key: 'faceMatch.validation.fileTooLarge', params: { maxMb: rule.maxBytes / MB } }
  return null
}

function typeLabel(type: string): string {
  if (type === 'application/pdf') return 'PDF'
  if (type === 'image/png') return 'PNG'
  return 'JPG'
}

/** 照片集合：张数范围 + 逐张类型 / 大小。返回第一条问题；通过返回 null。 */
export function validatePhotoSet(
  files: ReadonlyArray<UploadFileLike>,
  range: { min: number; max: number },
  rule: UploadRule = PHOTO_RULE,
): ValidationIssue | null {
  if (files.length < range.min || files.length > range.max) {
    return { key: 'faceMatch.validation.photoCountRange', params: { min: range.min, max: range.max } }
  }
  for (const file of files) {
    const issue = validateFile(file, rule)
    if (issue) return issue
  }
  return null
}

/** 选择文件时的筛选：已达上限的不再加入；返回可加入的文件和第一条被拒绝的原因（供提示）。 */
export function pickAcceptableFiles(
  incoming: ReadonlyArray<File>,
  currentCount: number,
  max: number,
  rule: UploadRule = PHOTO_RULE,
): { accepted: File[]; issue: ValidationIssue | null } {
  const accepted: File[] = []
  let issue: ValidationIssue | null = null
  for (const file of incoming) {
    if (currentCount + accepted.length >= max) {
      issue = issue ?? { key: 'faceMatch.validation.photoTooMany', params: { max } }
      break
    }
    const problem = validateFile(file, rule)
    if (problem) {
      issue = issue ?? problem
      continue
    }
    accepted.push(file)
  }
  return { accepted, issue }
}

export function formatFileSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '-'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < MB) return `${(bytes / 1024).toFixed(0)} KB`
  return `${(bytes / MB).toFixed(1)} MB`
}

// ───────────── 员工同意 ─────────────

export const CONSENT_METHOD_OPTIONS: ReadonlyArray<{ value: FaceConsentMethod; labelKey: string }> = [
  { value: 'paper', labelKey: 'faceMatch.consent.method.paper' },
  { value: 'electronic', labelKey: 'faceMatch.consent.method.electronic' },
]

export const CONSENT_METHOD_LABEL_KEYS: Record<string, string> = {
  paper: 'faceMatch.consent.method.paper',
  electronic: 'faceMatch.consent.method.electronic',
}

export interface FaceConsentFormValues {
  consentVersion: string
  /** 签署时间（UTC ISO）。 */
  signedAtUtc: string
  method: FaceConsentMethod
  /** 通知日期 yyyy-MM-dd。 */
  nswNoticeDate: string
  scan: File | null
}

export type FaceConsentFormErrors = Partial<Record<keyof FaceConsentFormValues, ValidationIssue>>

/** 通知日期到签署日期相隔的整天数（按日历日，忽略时刻）。 */
export function daysBetweenDates(from: string, to: string): number {
  return dayjs(to).startOf('day').diff(dayjs(from).startOf('day'), 'day')
}

/**
 * 记录同意的整表校验：
 * - NSW 员工必填通知日期，且通知日期到签署日期至少相隔 14 天（签署日不得晚于现在，所以通知也已满 14 天）；
 * - 纸质必须上传扫描件，电子可选；扫描件限 pdf / jpg / png、≤10MB。
 */
export function validateConsentForm(
  values: Partial<FaceConsentFormValues>,
  staff: Pick<FaceStaffCandidate, 'requiresNswNotice'>,
  now: Date = new Date(),
): FaceConsentFormErrors {
  const errors: FaceConsentFormErrors = {}
  const version = (values.consentVersion ?? '').trim()
  if (!version) errors.consentVersion = { key: 'faceMatch.validation.consentVersionRequired' }
  else if (version.length > CONSENT_VERSION_MAX_LENGTH) {
    errors.consentVersion = { key: 'faceMatch.validation.consentVersionTooLong', params: { max: CONSENT_VERSION_MAX_LENGTH } }
  }

  const signedAt = normalizeText(values.signedAtUtc)
  if (!signedAt || !dayjs(signedAt).isValid()) errors.signedAtUtc = { key: 'faceMatch.validation.signedAtRequired' }
  else if (dayjs(signedAt).isAfter(dayjs(now))) errors.signedAtUtc = { key: 'faceMatch.validation.signedAtFuture' }

  const noticeDate = normalizeText(values.nswNoticeDate)
  if (staff.requiresNswNotice) {
    if (!noticeDate) errors.nswNoticeDate = { key: 'faceMatch.validation.noticeDateRequired' }
    else if (signedAt && dayjs(signedAt).isValid() && daysBetweenDates(noticeDate, signedAt) < NSW_NOTICE_MIN_DAYS) {
      errors.nswNoticeDate = { key: 'faceMatch.validation.noticeTooRecent', params: { days: NSW_NOTICE_MIN_DAYS } }
    }
  }

  if (values.method !== 'paper' && values.method !== 'electronic') {
    errors.method = { key: 'faceMatch.validation.methodRequired' }
  } else if (values.method === 'paper' && !values.scan) {
    errors.scan = { key: 'faceMatch.validation.scanRequiredForPaper' }
  }
  if (values.scan) {
    const issue = validateFile(values.scan, CONSENT_SCAN_RULE)
    if (issue) errors.scan = issue
  }
  return errors
}

export function buildConsentInput(
  userGuid: string,
  values: FaceConsentFormValues,
  staff: Pick<FaceStaffCandidate, 'requiresNswNotice'>,
): CreateFaceConsentInput {
  return {
    userGuid,
    consentVersion: values.consentVersion.trim(),
    signedAtUtc: values.signedAtUtc,
    method: values.method,
    // 非 NSW 员工不需要通知日期：即使表单里残留也不提交，避免后端误判。
    nswNoticeDate: staff.requiresNswNotice ? normalizeText(values.nswNoticeDate) : null,
    scan: values.method === 'paper' || values.scan ? values.scan : null,
  }
}

/** 登记可用的同意：未撤回的，按签署时间新的在前。 */
export function pickEnrollableConsents<T extends { active: boolean; signedAtUtc: string }>(consents: ReadonlyArray<T>): T[] {
  return consents.filter((consent) => consent.active).sort((a, b) => b.signedAtUtc.localeCompare(a.signedAtUtc))
}

export const ENROLLMENT_ACTIVE = 1

export function isActiveEnrollment(enrollment: { status: number }) {
  return enrollment.status === ENROLLMENT_ACTIVE
}

// ───────────── 不受欢迎人员名单 ─────────────

export const CATEGORY_VIOLENCE = 1
export const CATEGORY_ORGANISED_THEFT = 2
export const CATEGORY_OTHER = 3

/** 事件类别枚举：没有「一般偷窃」这一项。 */
export const INCIDENT_CATEGORY_OPTIONS = [
  { value: CATEGORY_VIOLENCE, labelKey: 'faceMatch.unwelcome.category.violence' },
  { value: CATEGORY_ORGANISED_THEFT, labelKey: 'faceMatch.unwelcome.category.organisedTheft' },
  { value: CATEGORY_OTHER, labelKey: 'faceMatch.unwelcome.category.other' },
] as const

export const INCIDENT_CATEGORY_LABEL_KEYS: Record<number, string> = {
  [CATEGORY_VIOLENCE]: 'faceMatch.unwelcome.category.violence',
  [CATEGORY_ORGANISED_THEFT]: 'faceMatch.unwelcome.category.organisedTheft',
  [CATEGORY_OTHER]: 'faceMatch.unwelcome.category.other',
}

export const ENTRY_STATUS_PENDING = 0
export const ENTRY_STATUS_ACTIVE = 1

export const ENTRY_STATUS_LABEL_KEYS: Record<number, string> = {
  0: 'faceMatch.unwelcome.status.pending',
  1: 'faceMatch.unwelcome.status.active',
  2: 'faceMatch.unwelcome.status.expired',
  3: 'faceMatch.unwelcome.status.withdrawn',
}

export const ENTRY_STATUS_COLORS: Record<number, string> = {
  0: 'gold',
  1: 'green',
  2: 'default',
  3: 'default',
}

/** 状态筛选项（值即发给后端的数值）。 */
export const ENTRY_STATUS_FILTER_OPTIONS = [
  { value: ENTRY_STATUS_PENDING, labelKey: 'faceMatch.unwelcome.status.pending' },
  { value: ENTRY_STATUS_ACTIVE, labelKey: 'faceMatch.unwelcome.status.active' },
  { value: 2, labelKey: 'faceMatch.unwelcome.status.expired' },
  { value: 3, labelKey: 'faceMatch.unwelcome.status.withdrawn' },
] as const

export function canWithdrawEntry(entry: { status: number }) {
  return entry.status === ENTRY_STATUS_PENDING || entry.status === ENTRY_STATUS_ACTIVE
}

/** 到期日可选的最晚一天（今天起 12 个月，取当天 0 点）。 */
export function maxExpiryDay(now: Date = new Date()) {
  return dayjs(now).add(UNWELCOME_MAX_EXPIRY_MONTHS, 'month').startOf('day')
}

/** 到期日可选的最早一天（明天）。 */
export function minExpiryDay(now: Date = new Date()) {
  return dayjs(now).add(1, 'day').startOf('day')
}

export interface FaceUnwelcomeFormValues {
  incidentCategory: number | null
  categoryNote: string
  evidenceRef: string
  storeScope: string[]
  /** 到期日 yyyy-MM-dd。 */
  expiresOn: string
  photos: File[]
}

export type FaceUnwelcomeFormErrors = Partial<Record<keyof FaceUnwelcomeFormValues, ValidationIssue>>

/**
 * 提交条目校验（与后端硬条件一致）：类别必须是枚举（没有一般偷窃）；Other 必须说明（≤200）；
 * 证据编号必填（≤100）；至少一家生效门店；到期日必填且晚于今天、不超过 12 个月；照片 1–5 张。
 */
export function validateUnwelcomeEntryForm(
  values: Partial<FaceUnwelcomeFormValues>,
  now: Date = new Date(),
): FaceUnwelcomeFormErrors {
  const errors: FaceUnwelcomeFormErrors = {}
  const category = values.incidentCategory
  if (category !== CATEGORY_VIOLENCE && category !== CATEGORY_ORGANISED_THEFT && category !== CATEGORY_OTHER) {
    errors.incidentCategory = { key: 'faceMatch.validation.categoryRequired' }
  }
  const note = (values.categoryNote ?? '').trim()
  if (category === CATEGORY_OTHER && !note) errors.categoryNote = { key: 'faceMatch.validation.categoryNoteRequired' }
  else if (note.length > CATEGORY_NOTE_MAX_LENGTH) {
    errors.categoryNote = { key: 'faceMatch.validation.categoryNoteTooLong', params: { max: CATEGORY_NOTE_MAX_LENGTH } }
  }
  const evidence = (values.evidenceRef ?? '').trim()
  if (!evidence) errors.evidenceRef = { key: 'faceMatch.validation.evidenceRefRequired' }
  else if (evidence.length > EVIDENCE_REF_MAX_LENGTH) {
    errors.evidenceRef = { key: 'faceMatch.validation.evidenceRefTooLong', params: { max: EVIDENCE_REF_MAX_LENGTH } }
  }
  if (!values.storeScope || values.storeScope.length === 0) errors.storeScope = { key: 'faceMatch.validation.storeScopeRequired' }

  const expiresOn = normalizeText(values.expiresOn)
  if (!expiresOn || !dayjs(expiresOn).isValid()) errors.expiresOn = { key: 'faceMatch.validation.expiresRequired' }
  else if (dayjs(expiresOn).startOf('day').isBefore(minExpiryDay(now))) {
    errors.expiresOn = { key: 'faceMatch.validation.expiresPast' }
  } else if (dayjs(expiresOn).startOf('day').isAfter(maxExpiryDay(now))) {
    errors.expiresOn = { key: 'faceMatch.validation.expiresTooFar', params: { months: UNWELCOME_MAX_EXPIRY_MONTHS } }
  }

  const photoIssue = validatePhotoSet(values.photos ?? [], { min: UNWELCOME_PHOTO_MIN, max: UNWELCOME_PHOTO_MAX })
  if (photoIssue) errors.photos = photoIssue
  return errors
}

/** 到期日取所选日期当天 0 点（本地时区）的 UTC 时刻：保证最晚一天也不会超过后端「现在起 12 个月」的上限。 */
export function expiryToUtcIso(expiresOn: string): string {
  return dayjs(expiresOn).startOf('day').toDate().toISOString()
}

export function buildUnwelcomeEntryInput(values: FaceUnwelcomeFormValues): CreateFaceUnwelcomeEntryInput {
  return {
    incidentCategory: values.incidentCategory ?? CATEGORY_OTHER,
    // 只有 Other 才需要说明；其余类别即使残留说明也不提交。
    categoryNote: values.incidentCategory === CATEGORY_OTHER ? normalizeText(values.categoryNote) : null,
    evidenceRef: values.evidenceRef.trim(),
    storeScope: [...new Set(values.storeScope.map((code) => code.trim()).filter(Boolean))],
    expiresAtUtc: expiryToUtcIso(values.expiresOn),
    photos: values.photos,
  }
}

/** 批准进度文案用的数字，例如 1 / 2。 */
export function formatApprovalProgress(entry: { approvalsDone: number; approvalsNeeded: number }): string {
  return `${entry.approvalsDone} / ${entry.approvalsNeeded}`
}

/** 撤回原因 / 申诉备注等必填说明的校验。 */
export function validateRequiredText(value: string | null | undefined, max: number): ValidationIssue | null {
  const text = (value ?? '').trim()
  if (!text) return { key: 'faceMatch.validation.textRequired' }
  if (text.length > max) return { key: 'faceMatch.validation.textTooLong', params: { max } }
  return null
}

export function validateDisputeNote(value: string | null | undefined): ValidationIssue | null {
  const text = (value ?? '').trim()
  if (text.length > DISPUTE_NOTE_MAX_LENGTH) return { key: 'faceMatch.validation.textTooLong', params: { max: DISPUTE_NOTE_MAX_LENGTH } }
  return null
}

// ───────────── 复核任务：相似度、档位、两人复核流程 ─────────────

export const REVIEW_STATUS_PENDING = 0

export const REVIEW_STATUS_LABEL_KEYS: Record<number, string> = {
  0: 'faceMatch.review.status.pending',
  1: 'faceMatch.review.status.dismissed',
  2: 'faceMatch.review.status.confirmed',
}

export const REVIEW_STATUS_COLORS: Record<number, string> = {
  0: 'gold',
  1: 'default',
  2: 'red',
}

export const REVIEW_STATUS_FILTER_OPTIONS = [
  { value: 0, labelKey: 'faceMatch.review.status.pending' },
  { value: 1, labelKey: 'faceMatch.review.status.dismissed' },
  { value: 2, labelKey: 'faceMatch.review.status.confirmed' },
] as const

/** 档位文案：由后端 band 字段决定，页面不自己用阈值算。 */
export const BAND_LABEL_KEYS: Record<FaceUnwelcomeBand, string> = {
  high: 'faceMatch.review.band.high',
  normal: 'faceMatch.review.band.normal',
}

export const BAND_COLORS: Record<FaceUnwelcomeBand, string> = {
  high: 'red',
  normal: 'orange',
}

/** 相似度百分比：以后端整数 scorePercent 为准，夹在 0-100；绝不写成「概率」。 */
export function formatSimilarityPercent(task: Pick<FaceUnwelcomeTask, 'scorePercent'>): number {
  const value = Number.isFinite(task.scorePercent) ? task.scorePercent : 0
  return Math.min(100, Math.max(0, Math.round(value)))
}

export type ReviewStep = 'first' | 'second' | 'done'

/** nextStep：first 待第一复核人、second 待第二复核人、其余（none / 未知）视为已结束。 */
export function getReviewStep(task: Pick<FaceUnwelcomeTask, 'nextStep'>): ReviewStep {
  if (task.nextStep === 'first') return 'first'
  if (task.nextStep === 'second') return 'second'
  return 'done'
}

export const REVIEW_STEP_HINT_KEYS: Record<ReviewStep, string> = {
  first: 'faceMatch.review.step.first',
  second: 'faceMatch.review.step.second',
  done: 'faceMatch.review.step.done',
}

/** 是否可以提交判断：必须后端 canReview、图片还在、任务还没结束。 */
export function canSubmitVerdict(task: Pick<FaceUnwelcomeTask, 'canReview' | 'imageAvailable' | 'nextStep'>): boolean {
  return task.canReview && task.imageAvailable && getReviewStep(task) !== 'done'
}

/** 不能提交时给出原因文案键（按优先级）；可以提交返回 null。 */
export function getVerdictBlockReasonKey(
  task: Pick<FaceUnwelcomeTask, 'canReview' | 'imageAvailable' | 'nextStep'>,
): string | null {
  if (getReviewStep(task) === 'done') return 'faceMatch.review.blocked.done'
  if (!task.imageAvailable) return 'faceMatch.review.blocked.imageGone'
  if (!task.canReview) return 'faceMatch.review.blocked.cannotReview'
  return null
}

export const VERDICT_LABEL_KEYS = {
  match: 'faceMatch.review.verdict.match',
  notMatch: 'faceMatch.review.verdict.notMatch',
} as const

/** 参考照片切换项：0 .. referencePhotoCount-1。 */
export function referenceIndexes(task: Pick<FaceUnwelcomeTask, 'referencePhotoCount'>): number[] {
  return Array.from({ length: Math.max(0, task.referencePhotoCount) }, (_, index) => index)
}

// ───────────── 误报统计 ─────────────

/** 驳回率 = 已驳回 / (已驳回 + 已确认)；没有已结束任务时为 null。 */
export function computeDismissRate(dismissed: number, confirmed: number): number | null {
  const finished = dismissed + confirmed
  return finished > 0 ? dismissed / finished : null
}

/** 后端比率是 0-1 的小数；万一返回了 0-100 的百分数也按百分数处理，界面不显示超过 100%。 */
export function toRatio(value: number | null | undefined): number | null {
  if (value === null || value === undefined || !Number.isFinite(value) || value < 0) return null
  return value > 1 ? Math.min(1, value / 100) : value
}

export function formatRate(value: number | null | undefined): string {
  const ratio = toRatio(value)
  return ratio === null ? '-' : `${Math.round(ratio * 1000) / 10}%`
}

export function formatScoreBucket(bucket: { from: number; to: number }): string {
  return `${bucket.from}–${bucket.to}`
}

// ───────────── 客流统计 ─────────────

export interface TrafficRange {
  from: string
  to: string
}

export const DATE_FORMAT = 'YYYY-MM-DD'

/** 默认查最近 7 天（含今天）。 */
export function defaultTrafficRange(now: Date = new Date()): TrafficRange {
  return { from: dayjs(now).subtract(6, 'day').format(DATE_FORMAT), to: dayjs(now).format(DATE_FORMAT) }
}

export function validateTrafficQuery(store: string | null | undefined, range: Partial<TrafficRange>): ValidationIssue | null {
  if (!store) return { key: 'faceMatch.validation.storeRequired' }
  if (!range.from || !range.to) return { key: 'faceMatch.validation.rangeRequired' }
  const from = dayjs(range.from)
  const to = dayjs(range.to)
  if (!from.isValid() || !to.isValid() || from.isAfter(to)) return { key: 'faceMatch.validation.rangeInvalid' }
  if (to.diff(from, 'day') + 1 > TRAFFIC_MAX_RANGE_DAYS) {
    return { key: 'faceMatch.validation.rangeTooLong', params: { days: TRAFFIC_MAX_RANGE_DAYS } }
  }
  return null
}

export function formatHourRange(hour: number): string {
  const start = String(Math.min(23, Math.max(0, hour))).padStart(2, '0')
  return `${start}:00`
}

// ───────────── 审计 ─────────────

export function auditQueryFromFilters(filters: { store?: string | null; action?: string; range?: Partial<TrafficRange> }) {
  return {
    store: filters.store || undefined,
    action: normalizeText(filters.action) ?? undefined,
    from: filters.range?.from || undefined,
    to: filters.range?.to || undefined,
  }
}

export function canGoPrev(page: number) {
  return page > 1
}

export function canGoNext(hasMore: boolean) {
  return hasMore
}

/** 请求结果后页面是否需要「写入类操作」被禁用：功能未开启时只能查看。 */
export function isWriteBlocked(config: Pick<FaceMatchConfig, 'enabled'> | null | undefined): boolean {
  return !config?.enabled
}
