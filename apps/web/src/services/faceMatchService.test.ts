import assert from 'node:assert/strict'

import { RequestError } from '../utils/request'
import {
  approveUnwelcomeEntry,
  buildFaceConsentFormData,
  buildFaceEnrollmentFormData,
  buildUnwelcomeEntryFormData,
  checkFaceStoreProfile,
  createFaceConsent,
  createFaceEnrollment,
  createUnwelcomeEntry,
  fetchUnwelcomeReferenceImage,
  fetchUnwelcomeTaskImage,
  getFaceAudit,
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
  normalizeFaceGateReport,
  normalizeFaceMatchConfig,
  normalizeFaceTraffic,
  normalizeFaceUnwelcomeEntry,
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

  // ───────── 档案：列表 / 详情 / check / 保存 ─────────
  responseFactory = () => json({ success: true, data: [{ storeCode: '1008', mode: 2, evaluateMatch: true, crossBorderReviewValid: true }, null] })
  const profiles = await getFaceStoreProfiles()
  assert.equal(lastCall().url, '/api/react/v1/face-match/profiles')
  assert.equal(profiles.length, 2)
  assert.equal(profiles[0].mode, 2)
  assert.equal(profiles[0].crossBorderReviewValid, true)

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
  responseFactory = () => json({ success: true, data: { items: [taskPayload, { taskId: 'x', score: 61.6, band: 'weird', nextStep: undefined }], page: 1, pageSize: 20, hasMore: true } })
  const tasks = await getUnwelcomeTasks({ status: 0, page: 1, pageSize: 20 })
  assert.equal(lastCall().url, '/api/react/v1/face-match/unwelcome/tasks?status=0&page=1&pageSize=20')
  assert.equal(tasks.items[0].scorePercent, 87)
  assert.equal(tasks.items[0].band, 'high')
  assert.equal(tasks.items[0].referencePhotoCount, 3)
  assert.equal(tasks.items[1].band, 'normal', '未知档位按一般处理')
  assert.equal(tasks.items[1].scorePercent, 62, '缺 scorePercent 时由分数四舍五入')
  assert.equal(tasks.items[1].nextStep, 'none', '缺 nextStep 视为已结束')
  assert.equal(tasks.items[1].canReview, false)
  assert.equal(normalizeFaceUnwelcomeTask({ referencePhotoCount: -3 }).referencePhotoCount, 0)

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
