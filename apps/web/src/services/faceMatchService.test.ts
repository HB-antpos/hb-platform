import assert from 'node:assert/strict'

import { RequestError } from '../utils/request'
import {
  DEFAULT_UNWELCOME_AUTO_CONFIRM_SCORE,
  approveUnwelcomeEntry,
  buildFaceConsentFormData,
  buildFaceEnrollmentFormData,
  buildProfileRequestBody,
  buildUnwelcomeEntryFormData,
  checkFaceStoreProfile,
  createFaceConsent,
  createFaceEnrollment,
  createUnwelcomeEntry,
  fetchFaceCaptureResultImage,
  fetchFaceHitDetail,
  fetchUnwelcomeReferenceImage,
  fetchUnwelcomeTaskImage,
  getFaceAudit,
  getFaceCaptureResults,
  getFaceConsents,
  getFaceEnrollments,
  getFaceMatchConfig,
  getFaceStaff,
  getFaceStoreProfile,
  getFaceStoreProfiles,
  getFaceTraffic,
  getUnwelcomeEntries,
  getUnwelcomeStats,
  getUnwelcomeTasks,
  normalizeFaceCaptureResult,
  normalizeFaceGateReport,
  normalizeFaceHitDetail,
  normalizeFaceMatchConfig,
  normalizeFaceTraffic,
  normalizeFaceUnwelcomeEntry,
  normalizeFaceUnwelcomeStats,
  normalizeFaceUnwelcomeTask,
  reviewUnwelcomeTask,
  setUnwelcomeDisputeNote,
  updateFaceStoreProfile,
  withdrawFaceConsent,
  withdrawFaceEnrollment,
  withdrawUnwelcomeEntry,
} from './faceMatchService'
import type { FaceStoreProfileUpdateRequest } from '../types/faceMatch'

// 服务层测试：全部用假 fetch 与假数据，不连任何服务，也不碰真实图片。

const originalFetch = globalThis.fetch
const calls: Array<{ url: string; init?: RequestInit }> = []
let responseFactory: () => Response

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  calls.push({ url: String(input), init })
  return responseFactory()
}) as typeof fetch

// 监听所有 console 输出：上传内容 / 图片字节绝不能出现在任何日志里。
const consoleOutput: string[] = []
const consoleMethods = ['log', 'info', 'warn', 'error', 'debug', 'trace'] as const
const originalConsole = new Map<string, (...args: unknown[]) => void>()
for (const method of consoleMethods) {
  originalConsole.set(method, console[method])
  console[method] = (...args: unknown[]) => {
    consoleOutput.push(args.map((arg) => (typeof arg === 'string' ? arg : JSON.stringify(arg))).join(' '))
  }
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
}

function lastCall() {
  return calls[calls.length - 1]
}

function bodyOf(call: { init?: RequestInit }) {
  return call.init?.body === undefined ? undefined : JSON.parse(String(call.init.body))
}

const emptyProfileRequest: FaceStoreProfileUpdateRequest = {
  mode: 2,
  evaluateMatch: false,
  staffExclusionEnabled: true,
  trafficDedupEnabled: false,
  unwelcomeListEnabled: false,
  unwelcomeAutoConfirmScore: 85,
  allowedSubjects: null,
  piaReference: 'PIA-1',
  signageInstalledOn: '2026-10-01',
  staffNoticeDate: null,
  legalSignoffBy: 'Lawyer',
  legalSignoffAt: '2026-10-02T00:00:00.000Z',
  dedupLegalSignoffBy: null,
  dedupLegalSignoffAt: null,
  crossBorderRegion: 'ap-singapore',
  crossBorderReviewRef: 'XB-1',
  crossBorderReviewedBy: 'Lawyer',
  crossBorderReviewedAt: '2026-10-02T00:00:00.000Z',
  timeZoneId: null,
  unwelcomeAssessmentRef: null,
  unwelcomeLegalSignoffBy: null,
  unwelcomeLegalSignoffAt: null,
  staffTrainingDate: null,
  privacyContact: null,
}

const taskPayload = {
  taskId: 'T'.repeat(32),
  storeCode: '1008',
  storeName: 'Bankstown',
  cameraId: 'cam-1',
  capturedAtUtc: '2026-10-13T01:00:00Z',
  entryId: 'E'.repeat(32),
  score: 87.4,
  scorePercent: 87,
  band: 'high',
  reviewStatus: 0,
  reviewStatusName: 'Pending',
  firstReviewerName: null,
  firstReviewedAtUtc: null,
  secondReviewerName: null,
  secondReviewedAtUtc: null,
  imageAvailable: true,
  imageDeleteAtUtc: '2026-10-20T01:00:00Z',
  referencePhotoCount: 3,
  nextStep: 'first',
  canReview: true,
}

const entryPayload = {
  entryId: 'E'.repeat(32),
  incidentCategory: 3,
  incidentCategoryName: 'Other',
  categoryNote: 'note',
  evidenceRef: 'POL-1',
  storeScope: ['1008', 'x', 5],
  submittedById: 'u1',
  submittedByName: 'Sam',
  submittedAtUtc: '2026-10-13T00:00:00Z',
  approvedByName: null,
  approvedAtUtc: null,
  secondApprovedByName: null,
  secondApprovedAtUtc: null,
  approvalsNeeded: 2,
  approvalsDone: 1,
  reviewDueAtUtc: '2027-04-13T00:00:00Z',
  expiresAtUtc: '2027-10-01T00:00:00Z',
  status: 0,
  statusName: 'Pending',
  photoCount: 2,
  disputeNote: '',
  expiringSoon: true,
  daysToExpiry: 12,
  canApprove: true,
  cleanupPending: true,
}

try {
  // ───────── 配置：路径、no-store、权限标志、缺字段兜底 ─────────
  responseFactory = () =>
    json({
      success: true,
      data: {
        enabled: false,
        disabledReason: 'region-missing',
        region: null,
        allowedRegions: ['ap-singapore', 7],
        objectStoreConfigured: true,
        highBandScore: 90,
        captureRetentionDays: 7,
        permissions: { manage: true, trafficView: false, unwelcomeManage: true, unwelcomeApprove: false, unwelcomeReview: true },
      },
    })
  const config = await getFaceMatchConfig()
  assert.equal(lastCall().url, '/api/react/v1/face-match/config')
  assert.equal(lastCall().init?.method, 'GET')
  assert.equal(lastCall().init?.cache, 'no-store', '请求带 cache: no-store')
  assert.equal(config.enabled, false)
  assert.equal(config.disabledReason, 'region-missing')
  assert.deepEqual(config.allowedRegions, ['ap-singapore'])
  assert.equal(config.permissions.manage, true)
  assert.equal(config.permissions.unwelcomeApprove, false)
  assert.equal(config.highBandScore, 90)
  const emptyConfig = normalizeFaceMatchConfig(undefined)
  assert.equal(emptyConfig.enabled, false, '缺字段按未开启处理')
  assert.equal(emptyConfig.permissions.manage, false, '缺权限标志按无权限处理')
  // resultsView（FaceCapture.Images.View）：老后端没有这个字段按无权限；只认明确的 true
  assert.equal(config.permissions.resultsView, false, '老后端没有 resultsView：按 false，不显示「抓拍与比对」')
  assert.equal(emptyConfig.permissions.resultsView, false)
  assert.equal(normalizeFaceMatchConfig({ permissions: { resultsView: true } }).permissions.resultsView, true)
  assert.equal(normalizeFaceMatchConfig({ permissions: { resultsView: 'true' } }).permissions.resultsView, false, '字符串 true 不算')

  // ───────── 档案：列表 / 详情 / check / 保存 ─────────
  responseFactory = () => json({ success: true, data: [{ storeCode: '1008', mode: 2, evaluateMatch: true, crossBorderReviewValid: true }, null] })
  const profiles = await getFaceStoreProfiles()
  assert.equal(lastCall().url, '/api/react/v1/face-match/profiles')
  assert.equal(profiles.length, 2)
  assert.equal(profiles[0].mode, 2)
  assert.equal(profiles[0].crossBorderReviewValid, true)
  assert.equal(profiles[0].gateWarningCount, 0, '老后端的列表项没有 gateWarningCount：归一化为 0')
  responseFactory = () => json({ success: true, data: [{ storeCode: '1008', mode: 2, gateWarningCount: 9 }, { storeCode: '1009', gateWarningCount: -3 }, { storeCode: '1010', gateWarningCount: 'x' }] })
  const warned = await getFaceStoreProfiles()
  assert.deepEqual(warned.map((item) => item.gateWarningCount), [9, 0, 0], '警告条数只认非负数字，其余按 0')

  const gatePayload = {
    facets: [
      { facet: 'match', requested: true, missing: ['piaReference', 'legalSignoffAt'] },
      { facet: 'trafficDedup', requested: false, missing: [] },
    ],
    canSave: false,
  }
  responseFactory = () =>
    json({
      success: true,
      data: { storeCode: '1008', storeName: 'Bankstown', exists: true, mode: 2, jurisdiction: 'NSW', crossBorderReviewValid: false, effectiveTimeZoneId: 'Australia/Sydney', gates: gatePayload },
    })
  const profile = await getFaceStoreProfile('10 08')
  assert.equal(lastCall().url, '/api/react/v1/face-match/profiles/10%2008', '门店编码要做 URL 编码')
  assert.equal(profile.exists, true)
  assert.equal(profile.jurisdiction, 'NSW')
  assert.equal(profile.gates.canSave, false)
  assert.deepEqual(profile.gates.facets[0].missing, ['piaReference', 'legalSignoffAt'])
  assert.equal(profile.piaReference, null, '缺字段归一化为 null')
  assert.equal(profile.unwelcomeAutoConfirmScore, 90, '老后端 / 没有档案行时没有阈值：取默认 90')
  assert.equal(DEFAULT_UNWELCOME_AUTO_CONFIRM_SCORE, 90)
  // 阈值的各种取值：生效值原样解析（0 = 不自动确认，不能被默认值盖掉）；不是数字的一律取默认
  for (const [raw, expected] of [[0, 0], [75.5, 75.5], [100, 100], [95, 95], [null, 90], ['80', 90], [Number.NaN, 90], [{}, 90]] as const) {
    responseFactory = () => json({ success: true, data: { storeCode: '1008', unwelcomeAutoConfirmScore: raw } })
    assert.equal((await getFaceStoreProfile('1008')).unwelcomeAutoConfirmScore, expected, `后端阈值 ${String(raw)} 解析为 ${expected}`)
  }

  responseFactory = () => json({ success: true, data: gatePayload })
  const abort = new AbortController()
  const report = await checkFaceStoreProfile('1008', emptyProfileRequest, abort.signal)
  assert.equal(lastCall().url, '/api/react/v1/face-match/profiles/1008/check')
  assert.equal(lastCall().init?.method, 'POST')
  assert.equal(lastCall().init?.signal, abort.signal)
  assert.deepEqual(bodyOf(lastCall()), emptyProfileRequest)
  assert.equal(report.canSave, false)

  responseFactory = () => json({ success: true, data: { storeCode: '1008', gates: { facets: [], canSave: true } } })
  const saved = await updateFaceStoreProfile('1008', emptyProfileRequest)
  assert.equal(lastCall().url, '/api/react/v1/face-match/profiles/1008')
  assert.equal(lastCall().init?.method, 'PUT')
  assert.deepEqual(bodyOf(lastCall()), emptyProfileRequest)
  assert.equal(saved.gates.canSave, true)
  // 保存请求体始终带命中自动确认阈值（数字）
  assert.equal(bodyOf(lastCall()).unwelcomeAutoConfirmScore, 85)
  assert.equal(bodyOf(lastCall()).mode, 2)
  // 阈值 0（不自动确认）也要带上，不能因为是 falsy 被丢掉
  await updateFaceStoreProfile('1008', { ...emptyProfileRequest, unwelcomeAutoConfirmScore: 0 })
  assert.equal(bodyOf(lastCall()).unwelcomeAutoConfirmScore, 0)
  assert.ok(String(lastCall().init?.body).includes('"unwelcomeAutoConfirmScore":0'))
  // 实时闸门检查与保存共用同一个请求体构造
  await checkFaceStoreProfile('1008', { ...emptyProfileRequest, unwelcomeAutoConfirmScore: 92.5 })
  assert.equal(bodyOf(lastCall()).unwelcomeAutoConfirmScore, 92.5)
  // 缺失 / 不是数字时写成 null（后端语义：保持库里原值），字段不省略；绝不改写成别的数字
  const legacyRequest = { ...emptyProfileRequest } as Partial<FaceStoreProfileUpdateRequest>
  delete legacyRequest.unwelcomeAutoConfirmScore
  await updateFaceStoreProfile('1008', legacyRequest as FaceStoreProfileUpdateRequest)
  assert.ok(String(lastCall().init?.body).includes('"unwelcomeAutoConfirmScore":null'), '缺失时请求体里仍有该字段（null）')
  assert.equal(bodyOf(lastCall()).unwelcomeAutoConfirmScore, null)
  assert.equal(buildProfileRequestBody({ ...emptyProfileRequest, unwelcomeAutoConfirmScore: Number.NaN }).unwelcomeAutoConfirmScore, null)
  assert.equal(buildProfileRequestBody({ ...emptyProfileRequest, unwelcomeAutoConfirmScore: 90 }).unwelcomeAutoConfirmScore, 90)
  assert.equal(buildProfileRequestBody(emptyProfileRequest).piaReference, 'PIA-1', '其余字段原样保留')

  // 闸门拒绝：409 + details，错误码与闸门报告都保留在 RequestError.payload 里
  responseFactory = () =>
    json({ success: false, message: 'gate', errorCode: 'FACE_MATCH_GATE_NOT_MET', details: gatePayload }, 409)
  await assert.rejects(
    () => updateFaceStoreProfile('1008', emptyProfileRequest),
    (error: unknown) =>
      error instanceof RequestError &&
      error.status === 409 &&
      (error.payload as { errorCode?: string }).errorCode === 'FACE_MATCH_GATE_NOT_MET' &&
      normalizeFaceGateReport((error.payload as { details?: unknown }).details).facets.length === 2,
  )
  assert.equal(normalizeFaceGateReport(null).canSave, false, '缺闸门报告按不可保存处理')
  assert.equal(normalizeFaceGateReport(null).hasWarnings, false, '缺闸门报告没有警告')
  // 老后端（还没有 warnings / hasWarnings）：缺省为空数组 / false，页面不崩
  const legacyReport = normalizeFaceGateReport(gatePayload)
  assert.deepEqual(legacyReport.facets[0].warnings, [])
  assert.equal(legacyReport.hasWarnings, false)
  // 新后端：警告与阻塞缺项并存，canSave 只由后端给的标志决定
  const warnedReport = normalizeFaceGateReport({
    facets: [
      { facet: 'match', requested: true, missing: [], warnings: ['piaReference', 'regionMismatch', 7, ''] },
      { facet: 'unwelcome', requested: true, missing: ['modeOff'] },
    ],
    canSave: true,
    hasWarnings: true,
  })
  assert.deepEqual(warnedReport.facets[0].warnings, ['piaReference', 'regionMismatch'], '警告只保留非空字符串')
  assert.deepEqual(warnedReport.facets[1].warnings, [])
  assert.equal(warnedReport.canSave, true, '有警告但没有阻塞缺项时仍可保存')
  assert.equal(warnedReport.hasWarnings, true)
  assert.equal(
    normalizeFaceGateReport({ facets: [{ facet: 'match', requested: true, warnings: ['piaReference'] }], canSave: true }).hasWarnings,
    true,
    '报告没带 hasWarnings 但 facet 有警告：以实际警告条目为准',
  )

  // 保存成功（200）的响应里带警告：归一化后保留
  responseFactory = () =>
    json({
      success: true,
      data: { storeCode: '1008', gates: { facets: [{ facet: 'match', requested: true, missing: [], warnings: ['piaReference'] }], canSave: true, hasWarnings: true } },
    })
  const savedWithWarnings = await updateFaceStoreProfile('1008', emptyProfileRequest)
  assert.equal(savedWithWarnings.gates.canSave, true)
  assert.equal(savedWithWarnings.gates.hasWarnings, true)
  assert.deepEqual(savedWithWarnings.gates.facets[0].warnings, ['piaReference'])

  // ───────── 员工：查询参数 ─────────
  responseFactory = () => json({ success: true, data: [{ userGuid: 'u1', displayName: 'Ann', email: '', enrollmentState: 'active', requiresNswNotice: true }] })
  const staff = await getFaceStaff('1008', '  an ')
  assert.equal(lastCall().url, '/api/react/v1/face-match/staff?storeCode=1008&keyword=an')
  assert.equal(staff[0].email, null)
  assert.equal(staff[0].requiresNswNotice, true)
  await getFaceStaff('1008', '   ')
  assert.equal(lastCall().url, '/api/react/v1/face-match/staff?storeCode=1008', '空关键字不带 keyword')

  // ───────── 同意：multipart（字段名 scan），撤回 ─────────
  const scan = new File([new Uint8Array([1, 2, 3])], 'consent.pdf', { type: 'application/pdf' })
  const form = buildFaceConsentFormData({
    userGuid: 'u1',
    consentVersion: 'v1',
    signedAtUtc: '2026-10-01T00:00:00.000Z',
    method: 'paper',
    nswNoticeDate: '2026-09-01',
    scan,
  })
  assert.equal(form.get('userGuid'), 'u1')
  assert.equal(form.get('consentVersion'), 'v1')
  assert.equal(form.get('signedAtUtc'), '2026-10-01T00:00:00.000Z')
  assert.equal(form.get('method'), 'paper')
  assert.equal(form.get('nswNoticeDate'), '2026-09-01')
  assert.equal((form.get('scan') as File).name, 'consent.pdf', '同意书文件字段名为 scan')
  const formNoNotice = buildFaceConsentFormData({ userGuid: 'u1', consentVersion: 'v1', signedAtUtc: 'x', method: 'electronic', nswNoticeDate: null, scan: null })
  assert.equal(formNoNotice.has('nswNoticeDate'), false, '没有通知日期时不带该字段')
  assert.equal(formNoNotice.has('scan'), false, '电子同意可不带扫描件')

  responseFactory = () => json({ success: true, data: { consentId: 'c1', userGuid: 'u1', method: 'paper', active: true, hasEvidence: true } })
  const consent = await createFaceConsent({
    userGuid: 'u1', consentVersion: 'v1', signedAtUtc: '2026-10-01T00:00:00.000Z', method: 'paper', nswNoticeDate: null, scan,
  })
  assert.equal(lastCall().url, '/api/react/v1/face-match/consents')
  assert.equal(lastCall().init?.method, 'POST')
  assert.ok(lastCall().init?.body instanceof FormData, 'multipart 请求体是 FormData')
  assert.equal(consent.consentId, 'c1')
  assert.equal(consent.hasEvidence, true)
  // FormData 请求不能手动设置 Content-Type（要让运行时补 boundary）
  assert.equal(JSON.stringify(lastCall().init?.headers ?? {}).toLowerCase().includes('content-type'), false)

  responseFactory = () => json({ success: true, data: [{ consentId: 'c1', active: false, withdrawnAtUtc: '2026-10-05T00:00:00Z' }] })
  const consents = await getFaceConsents('u1', true)
  assert.equal(lastCall().url, '/api/react/v1/face-match/consents?userGuid=u1&includeWithdrawn=true')
  assert.equal(consents[0].active, false)

  responseFactory = () =>
    json({ success: true, data: { consent: { consentId: 'c1', active: false }, withdrawnEnrollmentCount: 1, pendingRemoteDeletionCount: 2 } })
  const withdrawn = await withdrawFaceConsent('c/1', 'left company')
  assert.equal(lastCall().url, '/api/react/v1/face-match/consents/c%2F1/withdraw')
  assert.equal(lastCall().init?.method, 'POST')
  assert.deepEqual(bodyOf(lastCall()), { reason: 'left company' })
  assert.equal(withdrawn.pendingRemoteDeletionCount, 2)
  assert.equal(withdrawn.withdrawnEnrollmentCount, 1)

  // ───────── 登记：photos 重复字段 ─────────
  const photos = [1, 2, 3].map((n) => new File([new Uint8Array([n])], `p${n}.jpg`, { type: 'image/jpeg' }))
  const enrollForm = buildFaceEnrollmentFormData({ userGuid: 'u1', consentId: 'c1', photos })
  assert.equal(enrollForm.get('consentId'), 'c1')
  assert.deepEqual((enrollForm.getAll('photos') as File[]).map((file) => file.name), ['p1.jpg', 'p2.jpg', 'p3.jpg'], '登记照字段名为 photos 且重复')

  responseFactory = () => json({ success: true, data: { enrollmentId: 'en1', status: 1, photoCount: 3, pendingRemoteDeletion: false } })
  const enrollment = await createFaceEnrollment({ userGuid: 'u1', consentId: 'c1', photos })
  assert.equal(lastCall().url, '/api/react/v1/face-match/enrollments')
  assert.ok(lastCall().init?.body instanceof FormData)
  assert.equal(enrollment.photoCount, 3)

  responseFactory = () => json({ success: true, data: [{ enrollmentId: 'en1', status: 1, pendingRemoteDeletion: true }] })
  const enrollments = await getFaceEnrollments('u1', false)
  assert.equal(lastCall().url, '/api/react/v1/face-match/enrollments?userGuid=u1&activeOnly=false')
  assert.equal(enrollments[0].pendingRemoteDeletion, true)

  responseFactory = () => json({ success: true, data: { enrollmentId: 'en1', status: 2 } })
  await withdrawFaceEnrollment('en1', 'withdrawn')
  assert.equal(lastCall().url, '/api/react/v1/face-match/enrollments/en1/withdraw')
  assert.deepEqual(bodyOf(lastCall()), { reason: 'withdrawn' })

  // ───────── 客流：查询参数；唯一访客始终是估算 ─────────
  responseFactory = () =>
    json({
      success: true,
      data: {
        storeCode: '1008', timeZoneId: 'Australia/Sydney', from: '2026-10-01', to: '2026-10-01',
        days: [{ date: '2026-10-01', counts: { captureCount: 10, uniqueVisitorCount: 4 } }],
        hours: [{ hour: 9, counts: { captureCount: 3 } }],
        total: { captureCount: 10, staffMatchCount: 2, uniqueVisitorCount: 4, repeatCount: 1, lowQualityCount: 1, apiFailureCount: 0 },
      },
    })
  const traffic = await getFaceTraffic('1008', '2026-10-01', '2026-10-01')
  assert.equal(lastCall().url, '/api/react/v1/face-match/traffic?store=1008&from=2026-10-01&to=2026-10-01')
  assert.equal(traffic.days[0].counts.uniqueVisitorCount, 4)
  assert.equal(traffic.days[0].counts.staffMatchCount, 0, '缺计数按 0')
  assert.equal(traffic.hours[0].hour, 9)
  assert.equal(traffic.total.staffMatchCount, 2)
  assert.equal(traffic.uniqueVisitorIsEstimate, true, '后端漏了估算标志也按估算处理')
  assert.equal(normalizeFaceTraffic({ uniqueVisitorIsEstimate: false }).uniqueVisitorIsEstimate, false)

  // ───────── 审计：空过滤条件不进查询串 ─────────
  responseFactory = () => json({ success: true, data: { items: [{ auditId: 5, atUtc: '2026-10-01T00:00:00Z', actorName: 'A', action: 'profile.update', result: 'ok' }], page: 2, pageSize: 20, hasMore: true } })
  const audit = await getFaceAudit({ store: '', action: ' ', from: '2026-10-01', page: 2, pageSize: 20 })
  assert.equal(lastCall().url, '/api/react/v1/face-match/audit?from=2026-10-01&page=2&pageSize=20')
  assert.equal(audit.hasMore, true)
  assert.equal(audit.items[0].auditId, 5)
  assert.equal(audit.items[0].storeCode, null)

  // ───────── 名单条目：列表 / 提交 multipart / 批准 / 撤回 / 申诉备注 ─────────
  responseFactory = () => json({ success: true, data: { items: [entryPayload], page: 1, pageSize: 20, hasMore: false } })
  const entries = await getUnwelcomeEntries({ status: 0, storeCode: '1008', page: 1, pageSize: 20 })
  assert.equal(lastCall().url, '/api/react/v1/face-match/unwelcome/entries?status=0&storeCode=1008&page=1&pageSize=20')
  assert.deepEqual(entries.items[0].storeScope, ['1008', 'x'], '门店范围只保留字符串')
  assert.equal(entries.items[0].disputeNote, null)
  assert.equal(entries.items[0].canApprove, true)
  assert.equal(entries.items[0].approvalsNeeded, 2)
  assert.equal(entries.items[0].cleanupPending, true, '清理未完成标志')
  assert.equal(normalizeFaceUnwelcomeEntry({}).cleanupPending, false, '缺字段按没有待清理处理')
  await getUnwelcomeEntries({ page: 1, pageSize: 20 })
  assert.equal(lastCall().url, '/api/react/v1/face-match/unwelcome/entries?page=1&pageSize=20', '不筛选时不带 status')

  const entryForm = buildUnwelcomeEntryFormData({
    incidentCategory: 3,
    categoryNote: 'explained',
    evidenceRef: 'POL-1',
    storeScope: ['1008', '1010'],
    expiresAtUtc: '2027-01-01T00:00:00.000Z',
    photos: photos.slice(0, 2),
  })
  assert.equal(entryForm.get('incidentCategory'), '3')
  assert.equal(entryForm.get('categoryNote'), 'explained')
  assert.equal(entryForm.get('evidenceRef'), 'POL-1')
  assert.equal(entryForm.get('storeScope'), '1008,1010', '门店范围逗号分隔')
  assert.equal(entryForm.get('expiresAtUtc'), '2027-01-01T00:00:00.000Z')
  assert.equal(entryForm.getAll('photos').length, 2)
  assert.equal(
    buildUnwelcomeEntryFormData({ incidentCategory: 1, categoryNote: null, evidenceRef: 'x', storeScope: ['1'], expiresAtUtc: 'y', photos }).has('categoryNote'),
    false,
    '没有说明时不带 categoryNote',
  )

  responseFactory = () => json({ success: true, data: entryPayload })
  await createUnwelcomeEntry({ incidentCategory: 3, categoryNote: 'n', evidenceRef: 'POL-1', storeScope: ['1008'], expiresAtUtc: '2027-01-01T00:00:00.000Z', photos })
  assert.equal(lastCall().url, '/api/react/v1/face-match/unwelcome/entries')
  assert.equal(lastCall().init?.method, 'POST')
  assert.ok(lastCall().init?.body instanceof FormData)

  await approveUnwelcomeEntry('E1')
  assert.equal(lastCall().url, '/api/react/v1/face-match/unwelcome/entries/E1/approve')
  assert.equal(lastCall().init?.body, undefined)
  await withdrawUnwelcomeEntry('E1', 'mistake')
  assert.equal(lastCall().url, '/api/react/v1/face-match/unwelcome/entries/E1/withdraw')
  assert.deepEqual(bodyOf(lastCall()), { reason: 'mistake' })
  await setUnwelcomeDisputeNote('E1', 'handled')
  assert.equal(lastCall().url, '/api/react/v1/face-match/unwelcome/entries/E1/dispute-note')
  assert.deepEqual(bodyOf(lastCall()), { note: 'handled' })

  // ───────── 复核任务：列表 / 判断 / 统计 ─────────
  // 第三条是旧版后端遗留的「已一审、待二审」任务：nextStep 仍是 second，原样解析（界面按「待复核」显示），
  // 复核人姓名照常解析；canReview 完全以后端为准（这里是 true，前端不能因为已有复核人就改成 false）。
  const legacySecondTask = { ...taskPayload, taskId: 'legacy', nextStep: 'second', firstReviewerName: 'Sam', secondReviewerName: 'Alex', canReview: true }
  responseFactory = () => json({ success: true, data: { items: [taskPayload, { taskId: 'x', score: 61.6, band: 'weird', nextStep: undefined }, legacySecondTask], page: 1, pageSize: 20, hasMore: true } })
  const tasks = await getUnwelcomeTasks({ status: 0, page: 1, pageSize: 20 })
  assert.equal(lastCall().url, '/api/react/v1/face-match/unwelcome/tasks?status=0&page=1&pageSize=20')
  assert.equal(tasks.items[0].scorePercent, 87)
  assert.equal(tasks.items[0].band, 'high')
  assert.equal(tasks.items[0].referencePhotoCount, 3)
  assert.equal(tasks.items[1].band, 'normal', '未知档位按一般处理')
  assert.equal(tasks.items[1].scorePercent, 62, '缺 scorePercent 时由分数四舍五入')
  assert.equal(tasks.items[1].nextStep, 'none', '缺 nextStep 视为已结束')
  assert.equal(tasks.items[1].canReview, false)
  assert.equal(tasks.items[2].nextStep, 'second', '旧版遗留的 second 原样解析')
  assert.equal(tasks.items[2].firstReviewerName, 'Sam')
  assert.equal(tasks.items[2].secondReviewerName, 'Alex', 'secondReviewerName 仍解析（旧遗留任务）')
  assert.equal(tasks.items[2].canReview, true, 'canReview 以后端为准')
  assert.equal(normalizeFaceUnwelcomeTask({ referencePhotoCount: -3 }).referencePhotoCount, 0)
  // 自动确认标志：老后端 / 缺字段按 false（人工复核）；只有明确的 true 才算系统自动确认
  assert.equal(tasks.items[0].autoConfirmed, false, '缺少 autoConfirmed 字段按 false 解析')
  assert.equal(normalizeFaceUnwelcomeTask({}).autoConfirmed, false)
  assert.equal(normalizeFaceUnwelcomeTask({ autoConfirmed: 'true' }).autoConfirmed, false, '字符串 true 不算')
  assert.equal(normalizeFaceUnwelcomeTask({ autoConfirmed: 1 }).autoConfirmed, false)
  const autoTask = normalizeFaceUnwelcomeTask({
    ...taskPayload, reviewStatus: 2, reviewStatusName: 'confirmed', firstReviewerName: null, firstReviewedAtUtc: null,
    nextStep: 'none', canReview: false, autoConfirmed: true,
  })
  assert.equal(autoTask.autoConfirmed, true)
  assert.equal(autoTask.reviewStatus, 2)
  assert.equal(autoTask.reviewStatusName, 'confirmed')
  assert.equal(autoTask.firstReviewerName, null)
  assert.equal(autoTask.canReview, false, '自动确认的任务没有复核入口')

  // 命中通知字段：老后端没有 → 排队中 / 0 人；新后端原样解析；负数、非数字按 0；未知状态保留原值供页面兜底
  assert.equal(tasks.items[0].notifyState, 'pending', '缺 notifyState 按排队中')
  assert.equal(tasks.items[0].notifiedAtUtc, null)
  assert.equal(tasks.items[0].notifyRecipientCount, 0)
  assert.equal(tasks.items[0].notifyReadCount, 0)
  const notified = normalizeFaceUnwelcomeTask({ ...taskPayload, notifyState: 'sent', notifiedAtUtc: '2026-10-14T01:00:05Z', notifyRecipientCount: 3, notifyReadCount: 1 })
  assert.deepEqual(
    [notified.notifyState, notified.notifiedAtUtc, notified.notifyRecipientCount, notified.notifyReadCount],
    ['sent', '2026-10-14T01:00:05Z', 3, 1],
  )
  const dirtyNotify = normalizeFaceUnwelcomeTask({ ...taskPayload, notifyState: '', notifiedAtUtc: 5, notifyRecipientCount: -2, notifyReadCount: 'x' })
  assert.deepEqual(
    [dirtyNotify.notifyState, dirtyNotify.notifiedAtUtc, dirtyNotify.notifyRecipientCount, dirtyNotify.notifyReadCount],
    ['pending', null, 0, 0],
  )
  assert.equal(normalizeFaceUnwelcomeTask({ ...taskPayload, notifyState: 'someNewState' }).notifyState, 'someNewState', '未知状态保留原值')

  responseFactory = () => json({ success: true, data: { ...taskPayload, reviewStatus: 1 } })
  await reviewUnwelcomeTask('T1', 'notMatch')
  assert.equal(lastCall().url, '/api/react/v1/face-match/unwelcome/tasks/T1/review')
  assert.equal(lastCall().init?.method, 'POST')
  assert.deepEqual(bodyOf(lastCall()), { verdict: 'notMatch' })

  responseFactory = () =>
    json({
      success: true,
      data: {
        taskCount: 12, pendingCount: 2, dismissedCount: 8, confirmedCount: 2, dismissRate: 0.8,
        entries: [{ entryId: 'E1', statusName: 'Active', taskCount: 10, dismissedCount: 8, confirmedCount: 2, dismissRate: 0.8, suggestReview: true }, { entryId: 'E2', dismissRate: null }],
        scoreBuckets: [{ from: 85, to: 89, total: 5, dismissedCount: 3, confirmedCount: 2 }],
      },
    })
  const stats = await getUnwelcomeStats()
  assert.equal(lastCall().url, '/api/react/v1/face-match/unwelcome/stats')
  assert.equal(stats.dismissRate, 0.8)
  assert.equal(stats.entries[0].suggestReview, true)
  assert.equal(stats.entries[1].dismissRate, null)
  assert.equal(stats.scoreBuckets[0].from, 85)
  // 老后端 / 字段缺失：三处 autoConfirmedCount 都按 0
  assert.equal(stats.autoConfirmedCount, 0, '总体缺 autoConfirmedCount 按 0')
  assert.equal(stats.entries[0].autoConfirmedCount, 0, '条目缺 autoConfirmedCount 按 0')
  assert.equal(stats.entries[1].autoConfirmedCount, 0)
  assert.equal(stats.scoreBuckets[0].autoConfirmedCount, 0, '相似度区间缺 autoConfirmedCount 按 0')
  assert.equal(normalizeFaceUnwelcomeStats(undefined).autoConfirmedCount, 0)
  // 新后端：自动确认单独计数，不算进「人工确认」；三处都解析
  responseFactory = () =>
    json({
      success: true,
      data: {
        taskCount: 20, pendingCount: 2, dismissedCount: 8, confirmedCount: 2, autoConfirmedCount: 8, dismissRate: 0.8,
        entries: [
          { entryId: 'E1', taskCount: 12, pendingCount: 1, dismissedCount: 8, confirmedCount: 1, autoConfirmedCount: 2, dismissRate: 8 / 9, suggestReview: true },
          { entryId: 'E2', taskCount: 8, autoConfirmedCount: 6, dismissRate: null },
        ],
        scoreBuckets: [
          { from: 85, to: 89, total: 5, dismissedCount: 3, confirmedCount: 2, autoConfirmedCount: 0 },
          { from: 95, to: 100, total: 6, dismissedCount: 0, confirmedCount: 0, autoConfirmedCount: 6 },
        ],
      },
    })
  const autoStats = await getUnwelcomeStats()
  assert.equal(autoStats.autoConfirmedCount, 8)
  assert.equal(autoStats.confirmedCount, 2, 'confirmedCount 只含人工确认')
  assert.equal(
    autoStats.taskCount,
    autoStats.pendingCount + autoStats.dismissedCount + autoStats.confirmedCount + autoStats.autoConfirmedCount,
    'taskCount = 待复核 + 已驳回 + 人工确认 + 自动确认',
  )
  assert.deepEqual(autoStats.entries.map((entry) => entry.autoConfirmedCount), [2, 6])
  assert.equal(autoStats.entries[1].dismissRate, null, '没有人工结案的任务时驳回率为 null')
  assert.deepEqual(autoStats.scoreBuckets.map((bucket) => bucket.autoConfirmedCount), [0, 6])
  // 非数字 / 负数一律按 0，不能让页面显示 NaN 或负数
  const dirtyStats = normalizeFaceUnwelcomeStats({
    autoConfirmedCount: '8',
    entries: [{ autoConfirmedCount: Number.NaN }, { autoConfirmedCount: -3 }, { autoConfirmedCount: null }],
    scoreBuckets: [{ autoConfirmedCount: {} }, { autoConfirmedCount: 4 }],
  })
  assert.equal(dirtyStats.autoConfirmedCount, 0)
  assert.deepEqual(dirtyStats.entries.map((entry) => entry.autoConfirmedCount), [0, 0, 0])
  assert.deepEqual(dirtyStats.scoreBuckets.map((bucket) => bucket.autoConfirmedCount), [0, 4])

  // ───────── 复核图片：专用内容接口，只认 jpeg / png，带登录 Cookie 且 no-store ─────────
  const imageBytes = new Uint8Array([0xff, 0xd8, 0xff, 0xe0, 9, 9, 9])
  responseFactory = () => new Response(imageBytes, { status: 200, headers: { 'content-type': 'image/jpeg' } })
  const captureBlob = await fetchUnwelcomeTaskImage('T1')
  assert.equal(lastCall().url, '/api/react/v1/face-match/unwelcome/tasks/T1/image')
  assert.equal(lastCall().init?.credentials, 'include')
  assert.equal(lastCall().init?.cache, 'no-store')
  assert.equal(captureBlob.size, imageBytes.length)
  const controller = new AbortController()
  responseFactory = () => new Response(imageBytes, { status: 200, headers: { 'content-type': 'image/png; charset=binary' } })
  await fetchUnwelcomeReferenceImage('T1', 2, controller.signal)
  assert.equal(lastCall().url, '/api/react/v1/face-match/unwelcome/tasks/T1/reference/2')
  assert.equal(lastCall().init?.signal, controller.signal)

  // 内容类型不对（例如服务端返回了 HTML 或别的二进制）：当作失败，不当图片
  responseFactory = () => new Response('<html></html>', { status: 200, headers: { 'content-type': 'text/html' } })
  await assert.rejects(() => fetchUnwelcomeTaskImage('T1'), (error: unknown) => error instanceof RequestError && error.status === 502)

  // 失败：抛 RequestError，状态码与错误码保留；错误里不带任何响应字节
  responseFactory = () => json({ success: false, message: '图片已清理', errorCode: 'FACE_MATCH_IMAGE_GONE' }, 404)
  await assert.rejects(
    () => fetchUnwelcomeTaskImage('T1'),
    (error: unknown) =>
      error instanceof RequestError && error.status === 404 && (error.payload as { errorCode?: string }).errorCode === 'FACE_MATCH_IMAGE_GONE',
  )
  responseFactory = () => new Response('', { status: 503 })
  await assert.rejects(() => fetchUnwelcomeTaskImage('T1'), (error: unknown) => error instanceof RequestError && error.status === 503)

  // 编号不合法：不发请求（防路径穿越）
  const before = calls.length
  for (const badId of ['../x', 'a/b', '', 'a b', 'x'.repeat(65)]) {
    await assert.rejects(
      () => fetchUnwelcomeTaskImage(badId),
      (error: unknown) =>
        error instanceof RequestError && error.status === 404 && (error.payload as { errorCode?: string }).errorCode === 'FACE_MATCH_IMAGE_GONE',
      '非法编号合成的错误码与后端的 IMAGE_GONE 一致',
    )
    await assert.rejects(() => fetchUnwelcomeReferenceImage(badId, 0), (error: unknown) => error instanceof RequestError && error.status === 404)
  }
  for (const badIndex of [-1, 1.5, 10, Number.NaN]) {
    await assert.rejects(() => fetchUnwelcomeReferenceImage('T1', badIndex), (error: unknown) => error instanceof RequestError && error.status === 404)
  }
  assert.equal(calls.length, before, '非法编号 / 序号不应发出任何请求')

  // ───────── 抓拍与比对结果：列表 ─────────
  const hitPayload = {
    taskId: 'T'.repeat(32), scorePercent: 87.4, reviewStatus: 0, reviewStatusName: 'pending', imageAvailable: true,
    notifyState: 'sent', notifiedAtUtc: '2026-10-14T01:00:05Z', notifyRecipientCount: 3, notifyReadCount: 1,
  }
  const resultPayload = {
    captureId: 'abc123', storeCode: '1042', storeName: 'TestStore', cameraId: 's1042-entrance-01', capturedAtUtc: '2026-10-14T01:00:03Z',
    faceCount: 1, isDuplicate: false, evaluated: true, decision: 'newVisitor', staffScorePercent: 9, visitScorePercent: 30.6,
    faceImageId: 'f1', sceneImageId: 's1', hit: hitPayload,
  }
  responseFactory = () =>
    json({
      success: true,
      data: {
        items: [
          resultPayload,
          { captureId: 'abc124', storeCode: '1042', cameraId: 'cam', capturedAtUtc: '2026-10-14T01:01:00Z', evaluated: false, decision: 'staff', hit: null },
          { captureId: 'abc125', evaluated: true, decision: 'someNewDecision', staffScorePercent: 'x', visitScorePercent: 250, hit: { taskId: '' } },
          null,
        ],
        page: 2, pageSize: 20, hasMore: true,
      },
    })
  const results = await getFaceCaptureResults({
    storeCode: '1042', cameraId: 's1042-entrance-01', decision: 'newVisitor', hitOnly: true,
    fromUtc: '2026-10-13T13:00:00.000Z', toUtc: '2026-10-14T13:00:00.000Z', page: 2, pageSize: 20,
  })
  const resultUrl = new URL(lastCall().url, 'http://localhost')
  assert.equal(resultUrl.pathname, '/api/react/v1/face-match/results')
  assert.equal(lastCall().init?.method, 'GET')
  assert.equal(lastCall().init?.cache, 'no-store')
  assert.deepEqual(Object.fromEntries(resultUrl.searchParams), {
    storeCode: '1042', cameraId: 's1042-entrance-01', decision: 'newVisitor', hitOnly: 'true',
    fromUtc: '2026-10-13T13:00:00.000Z', toUtc: '2026-10-14T13:00:00.000Z', page: '2', pageSize: '20',
  })
  assert.equal(results.hasMore, true)
  assert.equal(results.page, 2)
  assert.equal(results.items.length, 4)
  const [first, second, third] = results.items
  assert.equal(first.captureId, 'abc123')
  assert.equal(first.visitScorePercent, 31, '相似度四舍五入成整数')
  assert.equal(first.faceImageId, 'f1')
  assert.deepEqual(
    [first.hit?.taskId.length, first.hit?.scorePercent, first.hit?.band, first.hit?.notifyState, first.hit?.notifyRecipientCount, first.hit?.notifyReadCount, first.hit?.imageAvailable],
    [32, 87, null, 'sent', 3, 1, true],
  )
  assert.equal(second.decision, null, '未评估时不保留判定')
  assert.equal(second.hit, null)
  assert.equal(second.staffScorePercent, null)
  assert.equal(second.faceImageId, null)
  assert.equal(third.decision, 'someNewDecision', '未知判定保留原值，由页面兜底')
  assert.equal(third.staffScorePercent, null, '非数字分数按没有分数')
  assert.equal(third.visitScorePercent, 100, '分数夹在 0-100')
  assert.equal(third.hit, null, '没有任务编号的命中当作没有命中')
  assert.equal(results.items[3].captureId, '', '脏数据行不崩')
  assert.equal(normalizeFaceCaptureResult(undefined).faceCount, 0)

  // 空筛选：条件一个都不带；pageSize 夹在 1-50
  responseFactory = () => json({ success: true, data: { items: [], page: 1, pageSize: 50, hasMore: false } })
  await getFaceCaptureResults({ storeCode: '', cameraId: '', decision: '', hitOnly: false, page: 1, pageSize: 200 })
  const emptyUrl = new URL(lastCall().url, 'http://localhost')
  assert.deepEqual(Object.fromEntries(emptyUrl.searchParams), { page: '1', pageSize: '50' }, '空条件与 hitOnly=false 都不带；pageSize 上限 50')
  await getFaceCaptureResults({ page: 1, pageSize: 0 })
  assert.equal(new URL(lastCall().url, 'http://localhost').searchParams.get('pageSize'), '1')
  const resultController = new AbortController()
  await getFaceCaptureResults({ page: 1, pageSize: 20 }, resultController.signal)
  assert.equal(lastCall().init?.signal, resultController.signal, '列表请求可中止')

  // 权限不足：403 保留状态码与错误码
  responseFactory = () => json({ success: false, message: 'x', errorCode: 'FACE_MATCH_FORBIDDEN' }, 403)
  await assert.rejects(() => getFaceCaptureResults({ page: 1, pageSize: 20 }), (error: unknown) => error instanceof RequestError && error.status === 403)

  // ───────── 抓拍与比对结果：图片（按需、专用接口、写审计由服务端负责） ─────────
  responseFactory = () => new Response(imageBytes, { status: 200, headers: { 'content-type': 'image/jpeg' } })
  const resultBlob = await fetchFaceCaptureResultImage('abc123', 'f1')
  assert.equal(lastCall().url, '/api/react/v1/face-match/results/abc123/images/f1')
  assert.equal(lastCall().init?.credentials, 'include')
  assert.equal(lastCall().init?.cache, 'no-store')
  assert.equal(resultBlob.size, imageBytes.length)
  const imageController = new AbortController()
  await fetchFaceCaptureResultImage('abc123', 's1', imageController.signal)
  assert.equal(lastCall().init?.signal, imageController.signal)
  // 错误信封保留错误码（页面按 FACE_IMAGE_* 分类）
  responseFactory = () => json({ success: false, message: 'x', errorCode: 'FACE_IMAGE_FORBIDDEN' }, 403)
  await assert.rejects(
    () => fetchFaceCaptureResultImage('abc123', 'f1'),
    (error: unknown) => error instanceof RequestError && error.status === 403 && (error.payload as { errorCode?: string }).errorCode === 'FACE_IMAGE_FORBIDDEN',
  )
  responseFactory = () => new Response('<html></html>', { status: 200, headers: { 'content-type': 'text/html' } })
  await assert.rejects(() => fetchFaceCaptureResultImage('abc123', 'f1'), (error: unknown) => error instanceof RequestError && error.status === 502)
  // 编号不合法：不发请求（防路径穿越）
  const resultImageCalls = calls.length
  for (const badId of ['../x', 'a/b', '', 'a b', 'x'.repeat(65)]) {
    await assert.rejects(() => fetchFaceCaptureResultImage(badId, 'f1'), (error: unknown) => error instanceof RequestError && error.status === 404)
    await assert.rejects(() => fetchFaceCaptureResultImage('abc123', badId), (error: unknown) => error instanceof RequestError && error.status === 404)
  }
  assert.equal(calls.length, resultImageCalls, '非法编号不应发出任何请求')

  // ───────── 抓拍详情抽屉：命中详情（目标图张数 + 通知收件人姓名与已读状态） ─────────
  const detailTaskId = 'a1'.repeat(16)
  responseFactory = () =>
    json({
      success: true,
      data: {
        taskId: detailTaskId,
        targetPhotoCount: 2,
        notificationRevoked: false,
        recipients: [
          { displayName: 'Alice', read: false },
          { displayName: 'Bob', read: false },
          { displayName: 'Carol', read: false },
          { displayName: '管理员', read: true, readAtUtc: '2026-10-15T01:23:45Z' },
        ],
      },
    })
  const detailController = new AbortController()
  const detail = await fetchFaceHitDetail(detailTaskId, detailController.signal)
  assert.equal(lastCall().url, `/api/react/v1/face-match/results/hits/${detailTaskId}/detail`)
  assert.equal(lastCall().init?.method, 'GET')
  assert.equal(lastCall().init?.cache, 'no-store', '含姓名的响应不让浏览器 HTTP 缓存保留')
  assert.equal(lastCall().init?.signal, detailController.signal, '详情请求可中止（抽屉关闭 / 切换抓拍时中止）')
  assert.equal(detail.taskId, detailTaskId)
  assert.equal(detail.targetPhotoCount, 2)
  assert.equal(detail.notificationRevoked, false)
  assert.deepEqual(
    detail.recipients.map((recipient) => [recipient.displayName, recipient.read, recipient.readAtUtc]),
    [['Alice', false, null], ['Bob', false, null], ['Carol', false, null], ['管理员', true, '2026-10-15T01:23:45Z']],
    '4 个收件人、只有管理员已读；未读的没有已读时间',
  )
  // 收件人对象只有三个字段，没有账号标识
  assert.deepEqual(Object.keys(detail.recipients[0]).sort(), ['displayName', 'read', 'readAtUtc'])
  assert.deepEqual(Object.keys(detail).sort(), ['notificationRevoked', 'recipients', 'targetPhotoCount', 'taskId'])

  // 缺字段 / 脏数据按安全默认值：收件人为空、目标图 0 张（不显示目标图）、未撤回
  for (const garbage of [undefined, null, 'x', 42, [], {}]) {
    const safe = normalizeFaceHitDetail(garbage)
    assert.deepEqual(safe, { taskId: '', targetPhotoCount: 0, notificationRevoked: false, recipients: [] })
  }
  assert.equal(normalizeFaceHitDetail({ targetPhotoCount: 'many' }).targetPhotoCount, 0, '张数不是数字：0')
  assert.equal(normalizeFaceHitDetail({ targetPhotoCount: -3 }).targetPhotoCount, 0, '张数为负：0')
  assert.equal(normalizeFaceHitDetail({ targetPhotoCount: Number.NaN }).targetPhotoCount, 0)
  assert.equal(normalizeFaceHitDetail({ targetPhotoCount: 2.9 }).targetPhotoCount, 2, '张数取整')
  assert.equal(normalizeFaceHitDetail({ targetPhotoCount: 99 }).targetPhotoCount, 10, '张数不超过读取函数允许的下标范围')
  assert.equal(normalizeFaceHitDetail({ notificationRevoked: 'yes' }).notificationRevoked, false, '只有布尔 true 才算已撤回')
  assert.equal(normalizeFaceHitDetail({ notificationRevoked: true }).notificationRevoked, true)
  assert.deepEqual(normalizeFaceHitDetail({ recipients: 'nope' }).recipients, [], '收件人不是数组：空')
  const dirty = normalizeFaceHitDetail({
    recipients: [
      null,
      'text',
      7,
      { displayName: '  Alice  ', read: true, readAtUtc: '2026-10-15T02:00:00Z' },
      { displayName: '', read: 'true' },
      { displayName: 42, read: false, readAtUtc: '2026-10-15T03:00:00Z' },
      { read: true },
      { displayName: 'Bob', userGuid: 'secret-guid', username: 'bob-login', email: 'bob@example.test', read: true, readAtUtc: 42 },
    ],
  }).recipients
  assert.deepEqual(
    dirty.map((recipient) => [recipient.displayName, recipient.read, recipient.readAtUtc]),
    [
      ['Alice', true, '2026-10-15T02:00:00Z'],
      ['-', false, null],            // 展示名为空：占位；read 不是布尔 true：未读
      ['-', false, null],            // 展示名不是字符串：占位；未读却带了时间：丢弃时间
      ['-', true, null],             // 缺展示名但已读：占位；没有时间
      ['Bob', true, null],           // 已读时间不是字符串：没有时间
    ],
    '非对象项直接丢弃；展示名去空白、缺失为 -',
  )
  assert.ok(!JSON.stringify(dirty).includes('secret-guid') && !JSON.stringify(dirty).includes('bob-login') && !JSON.stringify(dirty).includes('bob@example'), '即使后端误返回账号标识也不会带进界面数据')

  // 错误：403 / 404 保留状态码与错误码（页面静默提示，不弹错）
  responseFactory = () => json({ success: false, message: 'x', errorCode: 'FACE_MATCH_NOT_FOUND' }, 404)
  await assert.rejects(
    () => fetchFaceHitDetail(detailTaskId),
    (error: unknown) => error instanceof RequestError && error.status === 404 && (error.payload as { errorCode?: string }).errorCode === 'FACE_MATCH_NOT_FOUND',
  )
  responseFactory = () => json({ success: false, message: 'x', errorCode: 'FACE_MATCH_FORBIDDEN' }, 403)
  await assert.rejects(() => fetchFaceHitDetail(detailTaskId), (error: unknown) => error instanceof RequestError && error.status === 403)
  // 任务编号不合法：不发请求（防路径穿越），按任务不存在处理
  const detailCalls = calls.length
  for (const badId of ['../x', 'a/b', '', 'a b', 'x'.repeat(65), 'a?b=1']) {
    await assert.rejects(() => fetchFaceHitDetail(badId), (error: unknown) => error instanceof RequestError && error.status === 404)
  }
  assert.equal(calls.length, detailCalls, '非法任务编号不应发出任何请求')

  // ───────── 业务错误码保留在 payload 里供页面映射 ─────────
  responseFactory = () => json({ success: false, message: 'x', errorCode: 'FACE_MATCH_NSW_NOTICE_TOO_RECENT' }, 400)
  await assert.rejects(
    () => createFaceConsent({ userGuid: 'u1', consentVersion: 'v1', signedAtUtc: 'x', method: 'electronic', nswNoticeDate: null, scan: null }),
    (error: unknown) => error instanceof RequestError && (error.payload as { errorCode?: string }).errorCode === 'FACE_MATCH_NSW_NOTICE_TOO_RECENT',
  )

  // 全程没有任何 console 输出（上传内容、图片字节、响应体都不进日志）
  assert.deepEqual(consoleOutput, [], '服务层不得输出任何 console 日志')
} finally {
  globalThis.fetch = originalFetch
  for (const method of consoleMethods) console[method] = originalConsole.get(method) as never
}

console.log('faceMatchService.test: ok')
