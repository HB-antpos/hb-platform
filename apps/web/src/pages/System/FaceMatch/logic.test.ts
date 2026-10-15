import assert from 'node:assert/strict'

import dayjs from 'dayjs'

import type { FaceCaptureResult, FaceHitResult, FaceStoreProfile } from '../../../types/faceMatch'
import { RequestError } from '../../../utils/request'
import {
  HIT_STATUS_LABEL_KEYS,
  HIT_STATUS_VALUES,
  buildHitSummaryQuery,
  buildHitsQuery,
  collectThumbnailTaskIds,
  defaultHitsRange,
  hitStatusCount,
  hitToCaptureResult,
} from './hitsLogic'
import {
  AUTO_CONFIRM_SCORE_DEFAULT,
  AUTO_CONFIRM_SCORE_MAX,
  AUTO_CONFIRM_SCORE_MIN,
  AUTO_CONFIRM_SCORE_OFF,
  BAND_LABEL_KEYS,
  CATEGORY_OTHER,
  FACE_MATCH_ERROR_KEYS,
  GENERIC_ERROR_KEY,
  HIT_NOTIFICATION_TITLE,
  INCIDENT_CATEGORY_OPTIONS,
  MISSING_KEY_LABEL_KEYS,
  NOTIFY_STATE_LABEL_KEYS,
  PHOTO_MAX_BYTES,
  RESULT_DECISION_LABEL_KEYS,
  RESULT_DECISION_UNKNOWN_KEY,
  RESULT_DECISION_VALUES,
  REVIEW_STEP_HINT_KEYS,
  TARGET_NOTE_KEYS,
  buildConsentInput,
  buildHitNotificationBody,
  buildNotifyTimeline,
  buildProfileRequest,
  buildResultsQuery,
  buildUnwelcomeEntryInput,
  canGoReview,
  canSubmitVerdict,
  canWithdrawEntry,
  classifyFaceMatchError,
  clampReadCount,
  describeFaceMatchError,
  computeDismissRate,
  countMissing,
  countWarnings,
  defaultResultsRange,
  defaultTrafficRange,
  detectFileType,
  expiryToUtcIso,
  formatApprovalProgress,
  formatResultPercent,
  formatFileSize,
  formatRate,
  formatScoreBucket,
  formatSimilarityPercent,
  getDisabledReasonKey,
  getFaceMatchErrorCode,
  getGateReportFromError,
  getKnownFaceMatchErrorCode,
  getMissingLabelKey,
  getReviewStep,
  getVerdictBlockReasonKey,
  getVisibleTabs,
  hasResultImages,
  isSameProfileRequest,
  isWriteBlocked,
  maxExpiryDay,
  minExpiryDay,
  normalizeAutoConfirmScore,
  normalizeText,
  pickAcceptableFiles,
  pickEnrollableConsents,
  profileToFormValues,
  referenceIndexes,
  resolveNotifyState,
  resolveResultDecision,
  resolveTargetAreaView,
  resultsRangeToUtc,
  stepTargetIndex,
  summarizeGates,
  summarizeRecipients,
  toRatio,
  validateAutoConfirmScore,
  validateConsentForm,
  validateDisputeNote,
  validateFile,
  validatePhotoSet,
  validateRequiredText,
  validateTrafficQuery,
  validateUnwelcomeEntryForm,
  type FaceProfileFormValues,
} from './logic'

// 纯逻辑测试：全部用假数据，不连任何服务。
// 注意：日期相关断言只用本地日历日，不依赖运行机器的时区。

const none = { manage: false, trafficView: false, unwelcomeManage: false, unwelcomeApprove: false, unwelcomeReview: false, resultsView: false }

// ───────── 页签可见性：按权限，且顺序固定 ─────────
assert.deepEqual(getVisibleTabs(none), [])
assert.deepEqual(getVisibleTabs({ ...none, manage: true }), ['profiles', 'staff', 'audit'])
assert.deepEqual(getVisibleTabs({ ...none, trafficView: true }), ['traffic'])
assert.deepEqual(getVisibleTabs({ ...none, unwelcomeManage: true }), ['unwelcome', 'stats'])
assert.deepEqual(getVisibleTabs({ ...none, unwelcomeApprove: true }), ['unwelcome', 'stats'], '批准权限可看条目与统计')
assert.deepEqual(getVisibleTabs({ ...none, unwelcomeReview: true }), ['review', 'stats'], '复核权限看复核任务与统计，不看条目')
assert.deepEqual(
  getVisibleTabs({ manage: true, trafficView: true, unwelcomeManage: true, unwelcomeApprove: true, unwelcomeReview: true, resultsView: false }),
  ['profiles', 'staff', 'traffic', 'unwelcome', 'review', 'stats', 'audit'],
  '没有 resultsView 时不出现「抓拍与比对」',
)
// 抓拍与比对：只看 resultsView（FaceCapture.Images.View），排在复核任务之后、误报统计之前
assert.deepEqual(getVisibleTabs({ ...none, resultsView: true }), ['results'], '只有看图权限：只能看抓拍与比对')
assert.deepEqual(getVisibleTabs({ ...none, unwelcomeReview: true, resultsView: true }), ['review', 'results', 'stats'])
assert.deepEqual(
  getVisibleTabs({ manage: true, trafficView: true, unwelcomeManage: true, unwelcomeApprove: true, unwelcomeReview: true, resultsView: true }),
  ['profiles', 'staff', 'traffic', 'unwelcome', 'review', 'results', 'stats', 'audit'],
  '全部权限：抓拍与比对在复核任务与误报统计之间',
)
assert.deepEqual(getVisibleTabs({ ...none, manage: true, resultsView: true }), ['profiles', 'staff', 'results', 'audit'])

// ───────── 功能未开启 ─────────
assert.equal(getDisabledReasonKey({ disabledReason: 'region-missing' }), 'faceMatch.disabled.reasons.regionMissing')
assert.equal(getDisabledReasonKey({ disabledReason: 'credentials-invalid' }), 'faceMatch.disabled.reasons.credentialsInvalid')
assert.equal(getDisabledReasonKey({ disabledReason: 'something-new' }), 'faceMatch.disabled.reasons.unknown', '未知原因走通用说明')
assert.equal(getDisabledReasonKey({ disabledReason: null }), 'faceMatch.disabled.reasons.unknown')
assert.equal(isWriteBlocked({ enabled: false }), true)
assert.equal(isWriteBlocked({ enabled: true }), false)
assert.equal(isWriteBlocked(null), true, '没有配置时按只读处理')

// ───────── 门店档案：表单 ↔ 请求体 ─────────
function profile(overrides: Partial<FaceStoreProfile> = {}): FaceStoreProfile {
  return {
    storeCode: '1008', storeName: 'Bankstown', exists: true, mode: 2, evaluateMatch: false, staffExclusionEnabled: true,
    trafficDedupEnabled: false, unwelcomeListEnabled: false, unwelcomeAutoConfirmScore: 90, allowedSubjects: null, piaReference: 'PIA-1', signageInstalledOn: '2026-10-01',
    staffNoticeDate: null, legalSignoffBy: 'Lawyer', legalSignoffAt: '2026-10-02T00:00:00.000Z', dedupLegalSignoffBy: null, dedupLegalSignoffAt: null,
    crossBorderRegion: null, crossBorderReviewRef: null, crossBorderReviewedBy: null, crossBorderReviewedAt: null, timeZoneId: null,
    unwelcomeAssessmentRef: null, unwelcomeLegalSignoffBy: null, unwelcomeLegalSignoffAt: null, staffTrainingDate: null, privacyContact: null,
    updatedAtUtc: null, updatedBy: null, effectiveTimeZoneId: 'Australia/Sydney', jurisdiction: 'NSW', crossBorderReviewValid: false,
    gates: { facets: [], canSave: true, hasWarnings: false },
    ...overrides,
  }
}

const values = profileToFormValues(profile(), 'ap-singapore')
assert.equal(values.crossBorderRegion, 'ap-singapore', '地域为空时预填当前配置地域')
assert.equal(values.piaReference, 'PIA-1')
assert.equal(values.allowedSubjects, '', 'null 展示成空串')
assert.equal(profileToFormValues(profile({ crossBorderRegion: 'ap-guangzhou' }), 'ap-singapore').crossBorderRegion, 'ap-guangzhou', '已保存的地域不被悄悄改写')
assert.equal(profileToFormValues(profile(), null).crossBorderRegion, '')

const request = buildProfileRequest({ ...values, piaReference: '  PIA-2  ', allowedSubjects: '   ', staffNoticeDate: '' })
assert.equal(request.piaReference, 'PIA-2', '去首尾空白')
assert.equal(request.allowedSubjects, null, '全空白规范成 null')
assert.equal(request.staffNoticeDate, null, '空串规范成 null')
assert.equal(request.mode, 2)
assert.equal(request.crossBorderRegion, 'ap-singapore')
assert.equal(buildProfileRequest({}).mode, 0, '缺 mode 按关闭')
assert.equal(buildProfileRequest({}).evaluateMatch, false)
assert.equal(isSameProfileRequest(request, buildProfileRequest(values)), false)
assert.equal(isSameProfileRequest(buildProfileRequest(values), buildProfileRequest(values)), true)
// 请求体字段与后端 FaceStoreProfileUpdateRequest 完全一致（24 个字段，含命中自动确认阈值）
assert.equal(Object.keys(request).length, 24)
assert.equal(normalizeText('  a '), 'a')
assert.equal(normalizeText(undefined), null)

// ───────── 命中自动确认阈值：0 = 不自动确认，否则 50–100、最多 2 位小数 ─────────
assert.equal(AUTO_CONFIRM_SCORE_DEFAULT, 90, '默认阈值 90')
assert.equal(AUTO_CONFIRM_SCORE_OFF, 0)
assert.equal(AUTO_CONFIRM_SCORE_MIN, 50)
assert.equal(AUTO_CONFIRM_SCORE_MAX, 100)
for (const ok of [0, 50, 50.5, 89.99, 90, 99.99, 100, 50.01, 75.5]) {
  assert.equal(validateAutoConfirmScore(ok), null, `${ok} 合法`)
}
assert.equal(validateAutoConfirmScore(49.99)?.key, 'faceMatch.validation.autoConfirmScoreInvalid', '49.99 低于 50')
assert.equal(validateAutoConfirmScore(49)?.key, 'faceMatch.validation.autoConfirmScoreInvalid', '1–49 之间不合法（只有 0 例外）')
assert.equal(validateAutoConfirmScore(0.5)?.key, 'faceMatch.validation.autoConfirmScoreInvalid')
assert.equal(validateAutoConfirmScore(1)?.key, 'faceMatch.validation.autoConfirmScoreInvalid')
assert.equal(validateAutoConfirmScore(-1)?.key, 'faceMatch.validation.autoConfirmScoreInvalid', '负数不合法')
assert.equal(validateAutoConfirmScore(100.01)?.key, 'faceMatch.validation.autoConfirmScoreInvalid', '100.01 超过 100')
assert.equal(validateAutoConfirmScore(150)?.key, 'faceMatch.validation.autoConfirmScoreInvalid')
assert.equal(validateAutoConfirmScore(90.001)?.key, 'faceMatch.validation.autoConfirmScoreInvalid', '3 位小数不合法')
assert.equal(validateAutoConfirmScore(50.123)?.key, 'faceMatch.validation.autoConfirmScoreInvalid')
assert.equal(validateAutoConfirmScore(99.995)?.key, 'faceMatch.validation.autoConfirmScoreInvalid')
assert.deepEqual(validateAutoConfirmScore(49.99)?.params, { min: 50, max: 100 }, '提示里带区间')
for (const empty of [null, undefined, Number.NaN, Number.POSITIVE_INFINITY, '90', '', {}]) {
  assert.equal(validateAutoConfirmScore(empty)?.key, 'faceMatch.validation.autoConfirmScoreRequired', `${String(empty)} 视为没填数字`)
}
assert.equal(normalizeAutoConfirmScore(90), 90)
assert.equal(normalizeAutoConfirmScore(0), 0, '0 是合法值，不能被当成空')
assert.equal(normalizeAutoConfirmScore(49.99), null, '不合法的输入不会被改写成别的数字，只会是 null（保持原值）')
assert.equal(normalizeAutoConfirmScore(null), null)

// 加载档案：回填后端生效值；0（不自动确认）原样保留；没有档案行时后端给默认 90
assert.equal(values.unwelcomeAutoConfirmScore, 90)
assert.equal(profileToFormValues(profile({ unwelcomeAutoConfirmScore: 0 }), null).unwelcomeAutoConfirmScore, 0, '0 不会被默认值盖掉')
assert.equal(profileToFormValues(profile({ unwelcomeAutoConfirmScore: 72.5 }), null).unwelcomeAutoConfirmScore, 72.5)
assert.equal(profileToFormValues(profile({ exists: false, unwelcomeAutoConfirmScore: 90 }), null).unwelcomeAutoConfirmScore, 90, '档案行不存在时显示默认 90')

// 请求体始终带这个字段：合法数字原样带上；0 也带；输入不合法 / 缺失时是 null（不省略，也不改写成别的数字）
assert.equal(request.unwelcomeAutoConfirmScore, 90)
assert.equal(buildProfileRequest({ ...values, unwelcomeAutoConfirmScore: 0 }).unwelcomeAutoConfirmScore, 0)
assert.equal(buildProfileRequest({ ...values, unwelcomeAutoConfirmScore: 87.25 }).unwelcomeAutoConfirmScore, 87.25)
for (const body of [
  buildProfileRequest({}),
  buildProfileRequest({ ...values, unwelcomeAutoConfirmScore: null }),
  buildProfileRequest({ ...values, unwelcomeAutoConfirmScore: 30 }),
  buildProfileRequest({ ...values, unwelcomeAutoConfirmScore: 90.001 }),
]) {
  assert.ok('unwelcomeAutoConfirmScore' in body, '字段始终存在')
  assert.equal(body.unwelcomeAutoConfirmScore, null)
  assert.ok(JSON.stringify(body).includes('"unwelcomeAutoConfirmScore":null'), 'null 会序列化进 JSON，而不是被丢掉')
}
assert.ok(JSON.stringify(request).includes('"unwelcomeAutoConfirmScore":90'))
assert.equal(
  isSameProfileRequest(buildProfileRequest({ ...values, unwelcomeAutoConfirmScore: 80 }), buildProfileRequest(values)),
  false,
  '只改阈值也算有未保存的修改',
)

// ───────── 闸门缺项 ─────────
const report = {
  facets: [
    { facet: 'capture', requested: false, missing: [], warnings: [] },
    { facet: 'match', requested: true, missing: ['piaReference', 'staffNoticeTooRecent', 'brandNewKey'], warnings: [] },
    { facet: 'staffExclusion', requested: true, missing: [], warnings: [] },
  ],
  canSave: false,
  hasWarnings: false,
}
const rows = summarizeGates(report)
assert.equal(rows.length, 2, '只列出被请求启用的项')
assert.equal(rows[0].facet, 'match')
assert.equal(rows[0].facetLabelKey, 'faceMatch.gates.facets.match')
assert.equal(countMissing(rows), 3)
assert.equal(countWarnings(rows), 0, '没有警告时警告数为 0')
assert.deepEqual(summarizeGates(null), [])

// 合规记录警告：与阻塞缺项分开统计；未被请求启用的 facet 即使带警告也不计入
const warningReport = {
  facets: [
    { facet: 'capture', requested: false, missing: [], warnings: ['piaReference'] },
    { facet: 'match', requested: true, missing: [], warnings: ['piaReference', 'regionMismatch', 'staffNoticeTooRecent'] },
    { facet: 'trafficDedup', requested: true, missing: ['modeOff'], warnings: ['dedupLegalSignoffBy', 'dedupLegalSignoffAt'] },
    { facet: 'unwelcome', requested: true, missing: [], warnings: [] },
  ],
  canSave: false,
  hasWarnings: true,
}
const warningRows = summarizeGates(warningReport)
assert.equal(warningRows.length, 3)
assert.deepEqual(warningRows[0].warnings, ['piaReference', 'regionMismatch', 'staffNoticeTooRecent'])
assert.deepEqual(warningRows[1].missing, ['modeOff'], '同一 facet 的阻塞项与警告分列')
assert.equal(countWarnings(warningRows), 5, '警告数是各被请求 facet 的警告之和（同一字段在不同 facet 分别计数）')
assert.equal(countMissing(warningRows), 1, '阻塞缺项数不含警告')
assert.equal(countWarnings([]), 0)
assert.equal(getMissingLabelKey('piaReference'), 'faceMatch.profile.fields.piaReference', '字段键映射到字段名称')
assert.equal(getMissingLabelKey('regionMismatch'), 'faceMatch.gates.special.regionMismatch', '特殊键有各自说明')
assert.equal(getMissingLabelKey('brandNewKey'), null, '后端新增的键没有登记时返回 null，页面显示键名本身')
assert.equal(summarizeGates({ facets: [{ facet: 'future', requested: true, missing: [], warnings: [] }], canSave: true, hasWarnings: false })[0].facetLabelKey, null)
// 警告键复用同一套文案映射：字段键与 regionMismatch / staffNoticeTooRecent 都有文案
assert.equal(getMissingLabelKey('staffNoticeTooRecent'), 'faceMatch.gates.special.staffNoticeTooRecent')
// 契约里的每个字段键与特殊键都有文案
for (const key of [
  'mode', 'allowedSubjects', 'piaReference', 'signageInstalledOn', 'staffNoticeDate', 'legalSignoffBy', 'legalSignoffAt',
  'dedupLegalSignoffBy', 'dedupLegalSignoffAt', 'crossBorderRegion', 'crossBorderReviewRef', 'crossBorderReviewedBy',
  'crossBorderReviewedAt', 'timeZoneId', 'unwelcomeAssessmentRef', 'unwelcomeLegalSignoffBy', 'unwelcomeLegalSignoffAt',
  'staffTrainingDate', 'privacyContact', 'activeEnrollment', 'regionMismatch', 'staffNoticeTooRecent', 'modeOff', 'evaluateMatchRequiresCapture',
]) {
  assert.ok(MISSING_KEY_LABEL_KEYS[key], `缺项键 ${key} 必须有文案`)
}

// ───────── 错误分类：已知码 → 状态码 → 通用 ─────────
function fail(status: number, payload: unknown) {
  return new RequestError('x', status, payload)
}
assert.equal(getFaceMatchErrorCode(fail(409, { errorCode: 'FACE_MATCH_GATE_NOT_MET' })), 'FACE_MATCH_GATE_NOT_MET')
assert.equal(getFaceMatchErrorCode(fail(409, { code: 'FACE_MATCH_DISABLED' })), 'FACE_MATCH_DISABLED')
assert.equal(getFaceMatchErrorCode(new Error('x')), null)
assert.equal(getFaceMatchErrorCode(fail(500, null)), null)
assert.equal(getKnownFaceMatchErrorCode(fail(400, { errorCode: 'FACE_MATCH_UNHEARD_OF' })), null)
assert.equal(classifyFaceMatchError(fail(503, { errorCode: 'FACE_MATCH_DISABLED' })), 'faceMatch.errors.FACE_MATCH_DISABLED', '已知码优先于状态码')
assert.equal(classifyFaceMatchError(fail(409, { errorCode: 'FACE_MATCH_GATE_NOT_MET' })), 'faceMatch.errors.FACE_MATCH_GATE_NOT_MET')
assert.equal(classifyFaceMatchError(fail(409, { errorCode: 'FACE_MATCH_UNHEARD_OF', message: '后端文本不当唯一文案' })), 'faceMatch.errors.status409', '未知码按状态码给说明')
assert.equal(classifyFaceMatchError(fail(403, null)), 'faceMatch.errors.status403')
assert.equal(classifyFaceMatchError(fail(401, null)), 'faceMatch.errors.status401')
assert.equal(classifyFaceMatchError(fail(503, null)), 'faceMatch.errors.status503')
assert.equal(classifyFaceMatchError(fail(500, { errorCode: 'WHATEVER' })), GENERIC_ERROR_KEY, '再不行走通用文案')
assert.equal(classifyFaceMatchError(new Error('network')), GENERIC_ERROR_KEY)
assert.equal(classifyFaceMatchError(undefined), GENERIC_ERROR_KEY)
for (const key of Object.values(FACE_MATCH_ERROR_KEYS)) assert.ok(key.startsWith('faceMatch.errors.FACE_MATCH_'))

// 后端真实使用的错误码（HTTP 状态）：登记表必须与之一致，不多不少；已被替换的旧码不得残留
const REAL_ERROR_CODES = [
  'FACE_MATCH_DISABLED', 'FACE_MATCH_STORE_NOT_CONFIGURED',
  'FACE_MATCH_FORBIDDEN', 'FACE_MATCH_STORE_FORBIDDEN', 'FACE_MATCH_STORE_INVALID', 'FACE_MATCH_STORE_NOT_FOUND',
  'FACE_MATCH_FIELD_INVALID', 'FACE_MATCH_NOT_FOUND', 'FACE_MATCH_GATE_NOT_MET',
  'FACE_MATCH_CONSENT_WITHDRAWN', 'FACE_MATCH_NSW_NOTICE_REQUIRED', 'FACE_MATCH_NSW_NOTICE_TOO_RECENT',
  'FACE_MATCH_ENROLLMENT_EXISTS', 'FACE_MATCH_PHOTO_UNUSABLE', 'FACE_MATCH_WITHDRAW_PENDING',
  'FACE_MATCH_TENCENT_AUTH', 'FACE_MATCH_TENCENT_BUSY', 'FACE_MATCH_TENCENT_LIMIT', 'FACE_MATCH_TENCENT_CONFLICT',
  'FACE_MATCH_TENCENT_UNAVAILABLE', 'FACE_MATCH_TENCENT_ERROR',
  'FACE_MATCH_ENTRY_NOT_PENDING', 'FACE_MATCH_APPROVER_IS_SUBMITTER', 'FACE_MATCH_ALREADY_APPROVED_BY_YOU',
  'FACE_MATCH_ENTRY_EXPIRED', 'FACE_MATCH_UNWELCOME_NOT_ENABLED', 'FACE_MATCH_ENTRY_CHANGED',
  'FACE_MATCH_TASK_CLOSED', 'FACE_MATCH_SAME_REVIEWER', 'FACE_MATCH_TASK_CHANGED',
  'FACE_MATCH_IMAGE_GONE', 'FACE_MATCH_IMAGE_TOO_LARGE', 'FACE_MATCH_IMAGE_UNSUPPORTED',
  'FACE_MATCH_STORE_DENIED', 'FACE_MATCH_STORE_TIMEOUT', 'FACE_MATCH_STORE_ERROR',
  'FACE_MATCH_AUDIT_FAILED', 'FACE_MATCH_TIMEZONE_UNAVAILABLE',
]
assert.deepEqual(Object.keys(FACE_MATCH_ERROR_KEYS).sort(), [...REAL_ERROR_CODES].sort(), '错误码登记表与后端真实码一致')
for (const code of REAL_ERROR_CODES) {
  assert.equal(classifyFaceMatchError(fail(500, { errorCode: code })), `faceMatch.errors.${code}`, `${code} 有各自文案`)
}
for (const retired of [
  'FACE_MATCH_OBJECT_STORE_NOT_CONFIGURED', 'FACE_MATCH_FILE_INVALID', 'FACE_MATCH_CONSENT_REQUIRED', 'FACE_MATCH_CONSENT_NOT_FOUND',
  'FACE_MATCH_ENROLLMENT_NOT_FOUND', 'FACE_MATCH_ENTRY_NOT_FOUND', 'FACE_MATCH_TASK_NOT_FOUND', 'FACE_MATCH_ALREADY_ENROLLED',
  'FACE_MATCH_PHOTO_QUALITY', 'FACE_MATCH_REMOTE_FAILED', 'FACE_MATCH_SELF_APPROVAL', 'FACE_MATCH_ALREADY_APPROVED',
  'FACE_MATCH_STATE_CONFLICT', 'FACE_MATCH_REVIEW_SAME_PERSON', 'FACE_MATCH_IMAGE_EXPIRED', 'FACE_MATCH_IMAGE_NOT_FOUND',
]) {
  assert.equal(getKnownFaceMatchErrorCode(fail(409, { errorCode: retired })), null, `${retired} 已被后端替换，不应再登记`)
}
// 已退役的码即使还出现，也按状态码兜底
assert.equal(classifyFaceMatchError(fail(409, { errorCode: 'FACE_MATCH_STATE_CONFLICT' })), 'faceMatch.errors.status409')

// describeFaceMatchError：DISABLED 追加 details.reason 的具体原因，FIELD_INVALID 追加 details.field
const echo = (key: string, params?: Record<string, unknown>) => (params ? `${key}|${JSON.stringify(params)}` : key)
assert.equal(
  describeFaceMatchError(echo, fail(503, { errorCode: 'FACE_MATCH_DISABLED', details: { reason: 'credentials-missing' } })),
  'faceMatch.errors.FACE_MATCH_DISABLED faceMatch.disabled.reasons.credentialsMissing',
)
assert.equal(
  describeFaceMatchError(echo, fail(503, { errorCode: 'FACE_MATCH_DISABLED', details: { reason: 'brand-new-reason' } })),
  'faceMatch.errors.FACE_MATCH_DISABLED',
  '未知 reason 不展示',
)
assert.equal(describeFaceMatchError(echo, fail(503, { errorCode: 'FACE_MATCH_DISABLED' })), 'faceMatch.errors.FACE_MATCH_DISABLED')
assert.equal(
  describeFaceMatchError(echo, fail(400, { errorCode: 'FACE_MATCH_FIELD_INVALID', details: { field: 'photos' } })),
  'faceMatch.errors.FACE_MATCH_FIELD_INVALID' + 'faceMatch.errors.fieldSuffix|{"field":"photos"}',
)
assert.equal(
  describeFaceMatchError(echo, fail(400, { errorCode: 'FACE_MATCH_FIELD_INVALID', details: { field: '<script>alert(1)</script>' } })),
  'faceMatch.errors.FACE_MATCH_FIELD_INVALID',
  '字段名不是普通标识符时不展示',
)
assert.equal(describeFaceMatchError(echo, fail(404, { errorCode: 'FACE_MATCH_NOT_FOUND', message: '后端文本' })), 'faceMatch.errors.FACE_MATCH_NOT_FOUND')
assert.equal(describeFaceMatchError(echo, fail(502, { errorCode: 'UNKNOWN' })), GENERIC_ERROR_KEY, '未知码未知状态走通用文案')

// 409 闸门拒绝：从 details 取闸门报告，其它错误取不到
const gateError = fail(409, { errorCode: 'FACE_MATCH_GATE_NOT_MET', details: report })
const extracted = getGateReportFromError(gateError)
assert.equal(extracted?.canSave, false)
assert.deepEqual(extracted?.facets[1].missing, ['piaReference', 'staffNoticeTooRecent', 'brandNewKey'])
assert.deepEqual(extracted?.facets[1].warnings, [], '老后端的 details 没有 warnings：归一化为空数组')
assert.equal(extracted?.hasWarnings, false)
// 阻塞项与警告同时存在时，409 的 details 两者都保留
const withWarnings = getGateReportFromError(fail(409, { errorCode: 'FACE_MATCH_GATE_NOT_MET', details: warningReport }))
assert.deepEqual(withWarnings?.facets[2].missing, ['modeOff'])
assert.deepEqual(withWarnings?.facets[2].warnings, ['dedupLegalSignoffBy', 'dedupLegalSignoffAt'])
assert.equal(withWarnings?.hasWarnings, true)
assert.equal(getGateReportFromError(fail(409, { errorCode: 'FACE_MATCH_GATE_NOT_MET' })), null, '没有 details 取不到')
assert.equal(getGateReportFromError(fail(409, { errorCode: 'FACE_MATCH_ENTRY_CHANGED', details: report })), null, '别的错误码不取')
assert.equal(getGateReportFromError(new Error('x')), null)

// ───────── 上传校验 ─────────
const file = (name: string, type: string, size = 1000) => ({ name, type, size })
assert.equal(detectFileType(file('a.JPG', '')), 'image/jpeg', 'MIME 缺失时按扩展名')
assert.equal(detectFileType(file('a.png', '')), 'image/png')
assert.equal(detectFileType(file('a.pdf', '')), 'application/pdf')
assert.equal(detectFileType(file('a.exe', '')), '')
assert.equal(detectFileType(file('a', 'image/jpg')), 'image/jpeg')
assert.equal(validateFile(file('a.jpg', 'image/jpeg'), { types: ['image/jpeg', 'image/png'], maxBytes: PHOTO_MAX_BYTES }), null)
assert.equal(validateFile(file('a.gif', 'image/gif'), { types: ['image/jpeg', 'image/png'], maxBytes: PHOTO_MAX_BYTES })?.key, 'faceMatch.validation.fileType')
assert.equal(validateFile(file('a.jpg', 'image/jpeg', 0), { types: ['image/jpeg'], maxBytes: 10 })?.key, 'faceMatch.validation.fileEmpty')
assert.equal(validateFile(file('a.jpg', 'image/jpeg', 6 * 1024 * 1024), { types: ['image/jpeg'], maxBytes: PHOTO_MAX_BYTES })?.key, 'faceMatch.validation.fileTooLarge')
assert.equal(validateFile(file('a.jpg', 'image/jpeg', PHOTO_MAX_BYTES), { types: ['image/jpeg'], maxBytes: PHOTO_MAX_BYTES }), null, '恰好 5MB 通过')
assert.equal(formatFileSize(512), '512 B')
assert.equal(formatFileSize(2048), '2 KB')
assert.equal(formatFileSize(5 * 1024 * 1024), '5.0 MB')

const jpg = (n: number) => file(`p${n}.jpg`, 'image/jpeg')
assert.equal(validatePhotoSet([jpg(1), jpg(2)], { min: 3, max: 5 })?.key, 'faceMatch.validation.photoCountRange', '少于 3 张')
assert.equal(validatePhotoSet([jpg(1), jpg(2), jpg(3)], { min: 3, max: 5 }), null)
assert.equal(validatePhotoSet([1, 2, 3, 4, 5, 6].map(jpg), { min: 3, max: 5 })?.key, 'faceMatch.validation.photoCountRange', '多于 5 张')
assert.equal(validatePhotoSet([jpg(1), jpg(2), file('x.gif', 'image/gif')], { min: 3, max: 5 })?.key, 'faceMatch.validation.fileType', '逐张校验类型')
assert.equal(validatePhotoSet([], { min: 1, max: 5 })?.params?.min, 1)

const incoming = [jpg(1), file('bad.gif', 'image/gif'), jpg(2), jpg(3)] as unknown as File[]
const picked = pickAcceptableFiles(incoming, 3, 5)
assert.equal(picked.accepted.length, 2, '已有 3 张，最多再收 2 张')
assert.equal(picked.issue?.key, 'faceMatch.validation.fileType', '第一条被拒原因')
assert.equal(pickAcceptableFiles([jpg(1)] as unknown as File[], 5, 5).issue?.key, 'faceMatch.validation.photoTooMany')
assert.equal(pickAcceptableFiles([jpg(1)] as unknown as File[], 0, 5).issue, null)

// ───────── 同意：NSW 14 天、纸质必传扫描件 ─────────
// 用本地时间构造，断言按本地日历日，任何时区下结果一致。
const now = new Date(2026, 9, 13, 15, 0)
const pdfScan = new File([new Uint8Array([1])], 'consent.pdf', { type: 'application/pdf' })
const okConsent = {
  consentVersion: 'v1',
  signedAtUtc: new Date(2026, 9, 13, 12, 0).toISOString(),
  method: 'paper' as const,
  nswNoticeDate: '',
  scan: pdfScan,
}
assert.deepEqual(validateConsentForm(okConsent, { requiresNswNotice: false }, now), {}, '非 NSW 员工不需要通知日期')
assert.equal(validateConsentForm({ ...okConsent, consentVersion: '  ' }, { requiresNswNotice: false }, now).consentVersion?.key, 'faceMatch.validation.consentVersionRequired')
assert.equal(validateConsentForm({ ...okConsent, consentVersion: 'v'.repeat(51) }, { requiresNswNotice: false }, now).consentVersion?.key, 'faceMatch.validation.consentVersionTooLong')
assert.equal(validateConsentForm({ ...okConsent, signedAtUtc: '' }, { requiresNswNotice: false }, now).signedAtUtc?.key, 'faceMatch.validation.signedAtRequired')
assert.equal(validateConsentForm({ ...okConsent, signedAtUtc: new Date(2026, 9, 14, 3, 0).toISOString() }, { requiresNswNotice: false }, now).signedAtUtc?.key, 'faceMatch.validation.signedAtFuture')
assert.equal(validateConsentForm({ ...okConsent, scan: null }, { requiresNswNotice: false }, now).scan?.key, 'faceMatch.validation.scanRequiredForPaper', '纸质必传扫描件')
assert.equal(validateConsentForm({ ...okConsent, method: 'electronic', scan: null }, { requiresNswNotice: false }, now).scan, undefined, '电子同意扫描件可选')
assert.equal(validateConsentForm({ ...okConsent, method: undefined as never }, { requiresNswNotice: false }, now).method?.key, 'faceMatch.validation.methodRequired')
const bigScan = new File([new Uint8Array(11 * 1024 * 1024)], 'big.pdf', { type: 'application/pdf' })
assert.equal(validateConsentForm({ ...okConsent, scan: bigScan }, { requiresNswNotice: false }, now).scan?.key, 'faceMatch.validation.fileTooLarge', '扫描件超过 10MB')
const exe = new File([new Uint8Array([1])], 'x.exe', { type: 'application/x-msdownload' })
assert.equal(validateConsentForm({ ...okConsent, scan: exe }, { requiresNswNotice: false }, now).scan?.key, 'faceMatch.validation.fileType')

const nsw = { requiresNswNotice: true }
assert.equal(validateConsentForm(okConsent, nsw, now).nswNoticeDate?.key, 'faceMatch.validation.noticeDateRequired', 'NSW 员工必填通知日期')
assert.equal(
  validateConsentForm({ ...okConsent, nswNoticeDate: '2026-10-01' }, nsw, now).nswNoticeDate?.key,
  'faceMatch.validation.noticeTooRecent',
  '通知距签署 12 天，不满 14 天',
)
assert.equal(
  validateConsentForm({ ...okConsent, nswNoticeDate: '2026-09-30' }, nsw, now).nswNoticeDate?.key,
  'faceMatch.validation.noticeTooRecent',
  '通知 9/30、签署 10/13 只隔 13 天，仍不满 14 天',
)
assert.equal(validateConsentForm({ ...okConsent, nswNoticeDate: '2026-09-29' }, nsw, now).nswNoticeDate, undefined, '恰好 14 天通过')
assert.equal(validateConsentForm({ ...okConsent, nswNoticeDate: '2026-08-01' }, nsw, now).nswNoticeDate, undefined)

// 请求入参：非 NSW 员工即使残留通知日期也不提交
assert.equal(buildConsentInput('u1', { ...okConsent, nswNoticeDate: '2026-08-01', consentVersion: ' v1 ' }, { requiresNswNotice: false }).nswNoticeDate, null)
assert.equal(buildConsentInput('u1', { ...okConsent, nswNoticeDate: '2026-08-01' }, nsw).nswNoticeDate, '2026-08-01')
assert.equal(buildConsentInput('u1', { ...okConsent, consentVersion: ' v1 ' }, nsw).consentVersion, 'v1')
assert.equal(buildConsentInput('u1', { ...okConsent, method: 'electronic', scan: null }, nsw).scan, null)

// 登记可用同意：未撤回，新的在前
const consentList = [
  { id: 'a', active: true, signedAtUtc: '2026-01-01T00:00:00Z' },
  { id: 'b', active: false, signedAtUtc: '2026-09-01T00:00:00Z' },
  { id: 'c', active: true, signedAtUtc: '2026-06-01T00:00:00Z' },
]
assert.deepEqual(pickEnrollableConsents(consentList).map((item) => item.id), ['c', 'a'])

// ───────── 不受欢迎人员名单：类别枚举没有「一般偷窃」 ─────────
assert.deepEqual(INCIDENT_CATEGORY_OPTIONS.map((option) => option.value), [1, 2, 3])
assert.equal(INCIDENT_CATEGORY_OPTIONS.length, 3, '只有三个类别')
assert.equal(canWithdrawEntry({ status: 0 }), true)
assert.equal(canWithdrawEntry({ status: 1 }), true)
assert.equal(canWithdrawEntry({ status: 2 }), false)
assert.equal(canWithdrawEntry({ status: 3 }), false)
assert.equal(formatApprovalProgress({ approvalsDone: 1, approvalsNeeded: 2 }), '1 / 2')

const photoFiles = (count: number) =>
  Array.from({ length: count }, (_, index) => new File([new Uint8Array([index + 1])], `p${index}.jpg`, { type: 'image/jpeg' }))
const validEntry = {
  incidentCategory: 1,
  categoryNote: '',
  evidenceRef: 'POL-123',
  storeScope: ['1008'],
  expiresOn: '2027-01-15',
  photos: photoFiles(2),
}
assert.deepEqual(validateUnwelcomeEntryForm(validEntry, now), {})
assert.equal(validateUnwelcomeEntryForm({ ...validEntry, incidentCategory: null }, now).incidentCategory?.key, 'faceMatch.validation.categoryRequired')
assert.equal(validateUnwelcomeEntryForm({ ...validEntry, incidentCategory: 4 }, now).incidentCategory?.key, 'faceMatch.validation.categoryRequired', '枚举之外（含想象中的「一般偷窃」）不接受')
assert.equal(validateUnwelcomeEntryForm({ ...validEntry, incidentCategory: 0 }, now).incidentCategory?.key, 'faceMatch.validation.categoryRequired')
assert.equal(validateUnwelcomeEntryForm({ ...validEntry, incidentCategory: CATEGORY_OTHER }, now).categoryNote?.key, 'faceMatch.validation.categoryNoteRequired', 'Other 必须说明')
assert.equal(validateUnwelcomeEntryForm({ ...validEntry, incidentCategory: CATEGORY_OTHER, categoryNote: ' ' }, now).categoryNote?.key, 'faceMatch.validation.categoryNoteRequired')
assert.deepEqual(validateUnwelcomeEntryForm({ ...validEntry, incidentCategory: CATEGORY_OTHER, categoryNote: 'x'.repeat(200) }, now), {}, '说明 200 字符通过')
assert.equal(validateUnwelcomeEntryForm({ ...validEntry, incidentCategory: CATEGORY_OTHER, categoryNote: 'x'.repeat(201) }, now).categoryNote?.key, 'faceMatch.validation.categoryNoteTooLong')
assert.equal(validateUnwelcomeEntryForm({ ...validEntry, evidenceRef: '  ' }, now).evidenceRef?.key, 'faceMatch.validation.evidenceRefRequired', '证据编号必填')
assert.equal(validateUnwelcomeEntryForm({ ...validEntry, evidenceRef: 'x'.repeat(101) }, now).evidenceRef?.key, 'faceMatch.validation.evidenceRefTooLong')
assert.deepEqual(validateUnwelcomeEntryForm({ ...validEntry, evidenceRef: 'x'.repeat(100) }, now), {})
assert.equal(validateUnwelcomeEntryForm({ ...validEntry, storeScope: [] }, now).storeScope?.key, 'faceMatch.validation.storeScopeRequired', '至少一家门店')
assert.equal(validateUnwelcomeEntryForm({ ...validEntry, photos: [] }, now).photos?.key, 'faceMatch.validation.photoCountRange', '至少一张照片')
assert.equal(validateUnwelcomeEntryForm({ ...validEntry, photos: photoFiles(6) }, now).photos?.key, 'faceMatch.validation.photoCountRange', '至多五张照片')
assert.deepEqual(validateUnwelcomeEntryForm({ ...validEntry, photos: photoFiles(5) }, now), {})

// 到期日：必填、晚于今天、不超过 12 个月（按本地日历日）
assert.equal(validateUnwelcomeEntryForm({ ...validEntry, expiresOn: '' }, now).expiresOn?.key, 'faceMatch.validation.expiresRequired')
assert.equal(validateUnwelcomeEntryForm({ ...validEntry, expiresOn: '2026-10-13' }, now).expiresOn?.key, 'faceMatch.validation.expiresPast', '今天不行')
assert.equal(validateUnwelcomeEntryForm({ ...validEntry, expiresOn: '2026-10-12' }, now).expiresOn?.key, 'faceMatch.validation.expiresPast')
assert.deepEqual(validateUnwelcomeEntryForm({ ...validEntry, expiresOn: '2026-10-14' }, now), {}, '明天可以')
assert.deepEqual(validateUnwelcomeEntryForm({ ...validEntry, expiresOn: '2027-10-13' }, now), {}, '恰好 12 个月可以')
assert.equal(validateUnwelcomeEntryForm({ ...validEntry, expiresOn: '2027-10-14' }, now).expiresOn?.key, 'faceMatch.validation.expiresTooFar', '超过 12 个月不行')
assert.equal(minExpiryDay(now).format('YYYY-MM-DD'), '2026-10-14')
assert.equal(maxExpiryDay(now).format('YYYY-MM-DD'), '2027-10-13')
// 到期时刻取所选日期当天本地 0 点：最晚一天也不会超过后端「现在起 12 个月」
const expiryIso = expiryToUtcIso('2027-10-13')
assert.equal(new Date(expiryIso).getTime(), new Date(2027, 9, 13, 0, 0, 0, 0).getTime())
assert.ok(new Date(expiryIso).getTime() <= new Date(2027, 9, 13, 15, 0).getTime())

// 请求入参：门店去重去空；非 Other 不带说明
const entryInput = buildUnwelcomeEntryInput({ ...validEntry, categoryNote: 'leftover', storeScope: ['1008', ' 1008 ', '1010', ''] })
assert.equal(entryInput.categoryNote, null, '非 Other 即使残留说明也不提交')
assert.deepEqual(entryInput.storeScope, ['1008', '1010'])
assert.equal(entryInput.evidenceRef, 'POL-123')
assert.equal(entryInput.photos.length, 2)
assert.equal(buildUnwelcomeEntryInput({ ...validEntry, incidentCategory: CATEGORY_OTHER, categoryNote: ' why ' }).categoryNote, 'why')

// 必填说明 / 申诉备注
assert.equal(validateRequiredText('  ', 200)?.key, 'faceMatch.validation.textRequired')
assert.equal(validateRequiredText('x'.repeat(201), 200)?.key, 'faceMatch.validation.textTooLong')
assert.equal(validateRequiredText('ok', 200), null)
assert.equal(validateDisputeNote(''), null, '申诉备注可为空')
assert.equal(validateDisputeNote('x'.repeat(501))?.key, 'faceMatch.validation.textTooLong')

// ───────── 复核：相似度只展示整数百分比，档位以后端为准 ─────────
assert.equal(formatSimilarityPercent({ scorePercent: 87 }), 87)
assert.equal(formatSimilarityPercent({ scorePercent: 87.6 }), 88)
assert.equal(formatSimilarityPercent({ scorePercent: 130 }), 100, '夹在 0-100')
assert.equal(formatSimilarityPercent({ scorePercent: -5 }), 0)
assert.equal(formatSimilarityPercent({ scorePercent: Number.NaN }), 0)
assert.equal(BAND_LABEL_KEYS.high, 'faceMatch.review.band.high')
assert.equal(BAND_LABEL_KEYS.normal, 'faceMatch.review.band.normal')

assert.equal(getReviewStep({ nextStep: 'first' }), 'first')
assert.equal(getReviewStep({ nextStep: 'second' }), 'first', '旧版后端遗留的 second 当作待复核')
assert.equal(getReviewStep({ nextStep: 'none' }), 'done')
assert.equal(getReviewStep({ nextStep: 'something-new' }), 'done', '未知步骤按已结束，不给提交按钮')
assert.deepEqual(Object.keys(REVIEW_STEP_HINT_KEYS).sort(), ['done', 'first'], '复核只剩「待复核」「已结束」两个状态')
assert.equal(REVIEW_STEP_HINT_KEYS[getReviewStep({ nextStep: 'second' })], 'faceMatch.review.step.first', 'second 与 first 显示同一句话')
const taskBase = { canReview: true, imageAvailable: true, nextStep: 'first' }
assert.equal(canSubmitVerdict(taskBase), true)
// 旧版遗留的 second 任务：按钮是否可点只看后端 canReview，不再因为「自己已判断过」被前端禁用
assert.equal(canSubmitVerdict({ ...taskBase, nextStep: 'second' }), true)
assert.equal(canSubmitVerdict({ ...taskBase, nextStep: 'second', canReview: false }), false)
assert.equal(getVerdictBlockReasonKey({ ...taskBase, nextStep: 'second' }), null)
assert.equal(canSubmitVerdict({ ...taskBase, canReview: false }), false, 'canReview 由后端决定')
assert.equal(canSubmitVerdict({ ...taskBase, imageAvailable: false }), false)
assert.equal(canSubmitVerdict({ ...taskBase, nextStep: 'none' }), false)
assert.equal(getVerdictBlockReasonKey(taskBase), null)
assert.equal(getVerdictBlockReasonKey({ ...taskBase, nextStep: 'none' }), 'faceMatch.review.blocked.done')
assert.equal(getVerdictBlockReasonKey({ ...taskBase, imageAvailable: false }), 'faceMatch.review.blocked.imageGone')
assert.equal(getVerdictBlockReasonKey({ ...taskBase, canReview: false }), 'faceMatch.review.blocked.cannotReview')
assert.deepEqual(referenceIndexes({ referencePhotoCount: 3 }), [0, 1, 2])
assert.deepEqual(referenceIndexes({ referencePhotoCount: 0 }), [])
assert.deepEqual(referenceIndexes({ referencePhotoCount: -1 }), [])

// ───────── 误报统计 ─────────
assert.equal(computeDismissRate(8, 2), 0.8)
assert.equal(computeDismissRate(0, 0), null, '没有已结束任务时为 null')
assert.equal(computeDismissRate(0, 4), 0)
assert.equal(toRatio(0.8), 0.8)
assert.equal(toRatio(80), 0.8, '万一后端给的是百分数，也按百分数换算')
assert.equal(toRatio(250), 1, '不超过 100%')
assert.equal(toRatio(null), null)
assert.equal(toRatio(-1), null)
assert.equal(formatRate(0.8), '80%')
assert.equal(formatRate(0.8333), '83.3%')
assert.equal(formatRate(null), '-')
assert.equal(formatScoreBucket({ from: 85, to: 89 }), '85–89')

// ───────── 客流统计查询 ─────────
assert.deepEqual(defaultTrafficRange(new Date(2026, 9, 13, 9, 0)), { from: '2026-10-07', to: '2026-10-13' }, '最近 7 天（含今天）')
assert.equal(validateTrafficQuery(null, { from: '2026-10-01', to: '2026-10-02' })?.key, 'faceMatch.validation.storeRequired')
assert.equal(validateTrafficQuery('1008', {})?.key, 'faceMatch.validation.rangeRequired')
assert.equal(validateTrafficQuery('1008', { from: '2026-10-05', to: '2026-10-01' })?.key, 'faceMatch.validation.rangeInvalid')
assert.equal(validateTrafficQuery('1008', { from: '2026-10-01', to: '2026-10-01' }), null, '单日合法（会返回分小时明细）')
assert.equal(validateTrafficQuery('1008', { from: '2026-01-01', to: '2026-04-03' })?.key, 'faceMatch.validation.rangeTooLong', '93 天超过上限')
assert.equal(validateTrafficQuery('1008', { from: '2026-01-01', to: '2026-04-03' })?.params?.days, 92)
assert.equal(validateTrafficQuery('1008', { from: '2026-01-01', to: '2026-04-02' }), null, '恰好 92 天可以')

// ───────── 抓拍与比对：判定 / 通知 / 相似度展示 ─────────
const capture = (overrides: Partial<FaceCaptureResult> = {}): FaceCaptureResult => ({
  captureId: 'c1', storeCode: '1042', storeName: 'TestStore', cameraId: 'cam-1', capturedAtUtc: '2026-10-14T01:00:00Z',
  faceCount: 1, isDuplicate: false, evaluated: true, decision: 'newVisitor', staffScorePercent: 9, visitScorePercent: 31,
  faceImageId: 'f1', sceneImageId: 's1', hit: null, ...overrides,
})
for (const value of RESULT_DECISION_VALUES) {
  assert.ok(RESULT_DECISION_LABEL_KEYS[value].startsWith('faceMatch.results.decision.'), `判定 ${value} 有文案键`)
}
assert.deepEqual([...RESULT_DECISION_VALUES], ['staff', 'repeatVisitor', 'newVisitor', 'lowQuality', 'noMatch', 'notEvaluated'])
assert.equal(resolveResultDecision(capture({ decision: 'staff' })).labelKey, 'faceMatch.results.decision.staff')
assert.equal(resolveResultDecision(capture({ decision: 'lowQuality' })).labelKey, 'faceMatch.results.decision.lowQuality')
assert.equal(resolveResultDecision(capture({ evaluated: false, decision: null })).labelKey, 'faceMatch.results.decision.notEvaluated', '没有评估行显示未评估')
assert.equal(resolveResultDecision(capture({ evaluated: false, decision: 'staff' })).labelKey, 'faceMatch.results.decision.notEvaluated', '未评估时即使带了判定也按未评估')
assert.equal(resolveResultDecision(capture({ decision: null })).labelKey, 'faceMatch.results.decision.notEvaluated')
assert.equal(resolveResultDecision(capture({ decision: 'brandNewValue' })).labelKey, RESULT_DECISION_UNKNOWN_KEY, '未知判定走兜底而不是崩')
assert.equal(resolveResultDecision(capture({ decision: 'toString' })).labelKey, RESULT_DECISION_UNKNOWN_KEY, '原型属性名也不会被当成已知取值')

assert.equal(formatResultPercent(91), '91%')
assert.equal(formatResultPercent(8.6), '9%')
assert.equal(formatResultPercent(null), '—', '没有分数显示 —')
assert.equal(formatResultPercent(undefined), '—')
assert.equal(formatResultPercent(Number.NaN), '—')
assert.equal(formatResultPercent(130), '100%')
assert.equal(formatResultPercent(-5), '0%')

assert.deepEqual(Object.keys(NOTIFY_STATE_LABEL_KEYS).sort(), ['pending', 'sent', 'skipped'])
assert.deepEqual(resolveNotifyState('sent'), { labelKey: 'faceMatch.notify.sent', color: 'green', hintKey: null, showCounts: true })
assert.deepEqual(resolveNotifyState('pending'), { labelKey: 'faceMatch.notify.pending', color: 'gold', hintKey: 'faceMatch.notify.pendingHint', showCounts: false })
assert.deepEqual(resolveNotifyState('skipped'), { labelKey: 'faceMatch.notify.skipped', color: 'default', hintKey: 'faceMatch.notify.skippedHint', showCounts: false })
for (const unknown of ['weird', '', null, undefined, 'constructor']) {
  assert.deepEqual(resolveNotifyState(unknown as string), { labelKey: 'faceMatch.notify.unknown', color: 'default', hintKey: null, showCounts: false }, `未知通知状态 ${String(unknown)} 走兜底`)
}
assert.equal(clampReadCount(3, 1), 1)
assert.equal(clampReadCount(3, 9), 3, '已读数不超过收件人数')
assert.equal(clampReadCount(0, 2), 0)
assert.equal(clampReadCount(3, -1), 0)

// 通知文案：固定英文原文，百分比取整并夹在 0-100
assert.equal(HIT_NOTIFICATION_TITLE, 'Possible match alert')
assert.equal(
  buildHitNotificationBody(87),
  'Possible match (similarity 87%). The score is not the probability that this is the same person; a reviewer must check it.',
)
assert.ok(buildHitNotificationBody(86.6).includes('similarity 87%'))
assert.ok(buildHitNotificationBody(Number.NaN).includes('similarity 0%'))
assert.ok(buildHitNotificationBody(180).includes('similarity 100%'))

// 去复核 / 查看的可用性
const hit = { taskId: 't1', scorePercent: 87, band: null, reviewStatus: 0, reviewStatusName: 'pending', imageAvailable: true, notifyState: 'sent', notifiedAtUtc: '2026-10-14T01:00:05Z', notifyRecipientCount: 3, notifyReadCount: 1, autoConfirmed: false, unread: false }
assert.equal(canGoReview({ hit }), true)
assert.equal(canGoReview({ hit: { ...hit, imageAvailable: false } }), false, '命中图已删除不能去复核')
assert.equal(canGoReview({ hit: null }), false, '没有命中没有任务可复核')
assert.equal(hasResultImages({ faceImageId: 'f', sceneImageId: null }), true)
assert.equal(hasResultImages({ faceImageId: null, sceneImageId: null }), false)

// 通知时间线：只含人数与时间，已读不超过收件人
const sentSteps = buildNotifyTimeline(hit)
assert.deepEqual(sentSteps.map((step) => step.key), ['created', 'sent', 'read', 'review'])
assert.deepEqual(sentSteps[1].params, { count: 3 })
assert.equal(sentSteps[1].atUtc, '2026-10-14T01:00:05Z')
assert.deepEqual(sentSteps[2].params, { read: 1, total: 3 })
assert.equal(sentSteps[2].done, true)
assert.equal(sentSteps[3].labelKey, 'faceMatch.review.status.pending')
assert.equal(sentSteps[3].done, false)
assert.equal(sentSteps[3].detailKey, 'faceMatch.results.timeline.reviewPendingHint')
assert.deepEqual(buildNotifyTimeline({ ...hit, notifyReadCount: 0 })[2].done, false, '一个人都没读：空心点')
assert.deepEqual(buildNotifyTimeline({ ...hit, notifyReadCount: 99 })[2].params, { read: 3, total: 3 }, '脏数据：已读夹回收件人数')
assert.deepEqual(buildNotifyTimeline({ ...hit, notifyState: 'pending', notifiedAtUtc: null, notifyRecipientCount: 0, notifyReadCount: 0 }).map((step) => step.key), ['created', 'queued', 'review'])
assert.deepEqual(buildNotifyTimeline({ ...hit, notifyState: 'skipped', reviewStatus: 1 }).map((step) => step.key), ['created', 'skipped', 'review'])
assert.equal(buildNotifyTimeline({ ...hit, notifyState: 'skipped', reviewStatus: 1 })[2].labelKey, 'faceMatch.review.status.dismissed')
assert.equal(buildNotifyTimeline({ ...hit, reviewStatus: 2 })[3].done, true)
const unknownSteps = buildNotifyTimeline({ ...hit, notifyState: 'weird', reviewStatus: 9 })
assert.deepEqual(unknownSteps.map((step) => step.key), ['created', 'unknown', 'review'], '未知通知状态也能画出时间线')
assert.equal(unknownSteps[2].labelKey, 'faceMatch.results.timeline.reviewUnknown', '未知复核状态不崩')
assert.ok(!JSON.stringify(sentSteps).includes('Name'), '时间线里没有姓名字段')

// 时间范围与查询条件：本地时区起止 → UTC；终点分钟含在内（后端终点不含，所以加一分钟）
const defaultRange = defaultResultsRange(new Date(2026, 9, 14, 15, 30))
assert.equal(defaultRange.start.format('YYYY-MM-DD HH:mm'), '2026-10-14 00:00')
assert.equal(defaultRange.end.format('YYYY-MM-DD HH:mm'), '2026-10-14 23:59')
assert.deepEqual(resultsRangeToUtc(defaultRange), {
  fromUtc: dayjs(new Date(2026, 9, 14, 0, 0)).toISOString(),
  toUtc: dayjs(new Date(2026, 9, 15, 0, 0)).toISOString(),
})
assert.deepEqual(resultsRangeToUtc(null), {}, '清空时间范围：不带起止')
assert.deepEqual(resultsRangeToUtc({ start: dayjs('invalid'), end: dayjs() }), {})
const emptyFilters = { storeCode: null, cameraId: null, decision: null, hitOnly: false, range: null }
assert.deepEqual(buildResultsQuery(emptyFilters, 1, 20), {
  storeCode: undefined, cameraId: undefined, decision: undefined, hitOnly: undefined, page: 1, pageSize: 20,
}, '空筛选不带任何条件')
const fullQuery = buildResultsQuery({ storeCode: '1042', cameraId: 'cam-1', decision: 'staff', hitOnly: true, range: defaultRange }, 3, 20)
assert.equal(fullQuery.storeCode, '1042')
assert.equal(fullQuery.cameraId, 'cam-1')
assert.equal(fullQuery.decision, 'staff')
assert.equal(fullQuery.hitOnly, true)
assert.equal(fullQuery.page, 3)
assert.equal(fullQuery.fromUtc, dayjs(new Date(2026, 9, 14, 0, 0)).toISOString())
assert.equal(buildResultsQuery({ ...emptyFilters, decision: 'notAThing' }, 1, 20).decision, undefined, '不认识的判定不发给后端')

// 抓拍详情抽屉：接收人摘要与名单目标图区域
assert.deepEqual(summarizeRecipients([]), { total: 0, read: 0 })
assert.deepEqual(summarizeRecipients([{ read: false }, { read: false }, { read: false }, { read: true }]), { total: 4, read: 1 }, '4 人 1 人已读：已读 1 / 4')
assert.deepEqual(summarizeRecipients([{ read: true }, { read: true }]), { total: 2, read: 2 })

// 没有复核权限：不管详情读到什么，一律不显示、不读目标图
for (const status of ['loading', 'error', 'ready'] as const) {
  assert.equal(resolveTargetAreaView({ canViewTarget: false, status, targetPhotoCount: 3 }), 'noPermission', `无复核权限 + ${status}`)
}
assert.equal(resolveTargetAreaView({ canViewTarget: true, status: 'loading', targetPhotoCount: 0 }), 'loading', '详情读取中：还不知道有几张，不读图')
assert.equal(resolveTargetAreaView({ canViewTarget: true, status: 'error', targetPhotoCount: 0 }), 'failed', '详情读取失败：不读图')
assert.equal(resolveTargetAreaView({ canViewTarget: true, status: 'ready', targetPhotoCount: 0 }), 'gone', '0 张：已清理或条目已失效')
assert.equal(resolveTargetAreaView({ canViewTarget: true, status: 'ready', targetPhotoCount: 1 }), 'photos')
assert.equal(resolveTargetAreaView({ canViewTarget: true, status: 'ready', targetPhotoCount: 5 }), 'photos')
// 「photos」以外的每种提示都有文案键
for (const view of ['noPermission', 'loading', 'failed', 'gone'] as const) {
  assert.ok(TARGET_NOTE_KEYS[view].startsWith('faceMatch.results.drawer.'), `${view} 有提示文案键`)
}
assert.deepEqual(Object.keys(TARGET_NOTE_KEYS).sort(), ['failed', 'gone', 'loading', 'noPermission'])

// 上一张 / 下一张：夹在 0 .. count-1，不越界；没有照片时为 0
assert.equal(stepTargetIndex(0, 1, 3), 1)
assert.equal(stepTargetIndex(2, 1, 3), 2, '最后一张再往后：停在最后一张')
assert.equal(stepTargetIndex(0, -1, 3), 0, '第一张再往前：停在第一张')
assert.equal(stepTargetIndex(2, -1, 3), 1)
assert.equal(stepTargetIndex(1, 1, 0), 0)
assert.equal(stepTargetIndex(Number.NaN, 1, 3), 1, '非法下标按 0 起算')
assert.equal(stepTargetIndex(0, 1, 1), 0, '只有一张时哪也去不了')

// 表单值与请求体的键一一对应（编译期有类型保证，这里保证运行期不漏）
const formKeys = Object.keys(values as FaceProfileFormValues).sort().join(',')
assert.equal(formKeys, Object.keys(request).sort().join(','), '表单字段与请求体字段一一对应')

// ───────── 名单命中视图 ─────────
assert.deepEqual([...HIT_STATUS_VALUES], ['all', 'unread', 'pending', 'autoConfirmed', 'confirmed', 'dismissed'], '状态取值与后端 FaceHitStatuses 一致、顺序即按钮顺序')
assert.ok(HIT_STATUS_VALUES.every((status) => HIT_STATUS_LABEL_KEYS[status].startsWith('faceMatch.hits.status.')))
const hitSummary = { total: 19, unread: 3, pending: 0, autoConfirmed: 19, confirmed: 0, dismissed: 0 }
assert.equal(hitStatusCount(hitSummary, 'all'), 19, '全部 = total')
assert.equal(hitStatusCount(hitSummary, 'unread'), 3)
assert.equal(hitStatusCount(null, 'all'), null, '汇总还没到不显示数字')
const hitsRange = defaultHitsRange(new Date(2026, 9, 16, 9, 30))
assert.equal(hitsRange.start.format('YYYY-MM-DD HH:mm'), '2026-10-10 00:00', '默认最近 7 天（含今天）')
assert.equal(hitsRange.end.format('YYYY-MM-DD HH:mm'), '2026-10-16 23:59')
const hitsQuery = buildHitsQuery({ storeCode: '1042', status: 'unread', range: hitsRange }, 2, 20)
assert.equal(hitsQuery.hitStatus, 'unread')
assert.equal(hitsQuery.storeCode, '1042')
assert.equal(hitsQuery.page, 2)
assert.equal(hitsQuery.toUtc, hitsRange.end.add(1, 'minute').toISOString(), '终点分钟含在内')
assert.equal(buildHitsQuery({ storeCode: null, status: 'all', range: null }, 1, 20).hitStatus, undefined, 'all 不带状态')
assert.equal(buildHitsQuery({ storeCode: '', status: 'weird' as never, range: null }, 1, 20).hitStatus, undefined, '不认识的状态不发给后端')
assert.deepEqual(buildHitSummaryQuery({ storeCode: null, range: null }), { storeCode: undefined }, '汇总只有门店与时间')
assert.deepEqual(collectThumbnailTaskIds(['a', null, 'b', 'a', undefined, 'c'], 2), ['a', 'b'], '缩略图编号去重、保序、限量')
const hitRow: FaceHitResult = {
  taskId: 'T1', captureId: null, storeCode: '1042', storeName: 'TestStore', cameraId: 'cam', capturedAtUtc: '2026-10-16T08:19:08Z',
  scorePercent: 100, band: 'high', reviewStatus: 2, reviewStatusName: 'confirmed', autoConfirmed: true, imageAvailable: true,
  unread: true, notificationId: 'n1', notifyState: 'sent', notifiedAtUtc: '2026-10-16T08:19:10Z', notifyRecipientCount: 4, notifyReadCount: 1,
  canReview: false, decision: null, staffScorePercent: null, visitScorePercent: null, faceCount: null,
}
const adapted = hitToCaptureResult(hitRow)
assert.equal(adapted.captureId, 'T1', '没有抓拍记录时用任务编号作抽屉的 key')
assert.equal(adapted.faceImageId, null, '命中流不带抓拍图片编号，抽屉不读抓拍图')
assert.equal(adapted.evaluated, false, '没有对应抓拍记录 → 未评估')
assert.deepEqual([adapted.hit?.taskId, adapted.hit?.autoConfirmed, adapted.hit?.unread, adapted.hit?.notifyRecipientCount], ['T1', true, true, 4])
assert.equal(hitToCaptureResult({ ...hitRow, captureId: 'C1', decision: 'repeatVisitor', visitScorePercent: 99 }).evaluated, true)

console.log('FaceMatch logic.test: ok')
