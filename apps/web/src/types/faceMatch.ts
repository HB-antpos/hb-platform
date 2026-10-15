// 门店人脸比对服务管理端的前端类型（后端契约：/api/react/v1/face-match，services/backend/BlazorApp.Api/Features/FaceMatch/FaceMatchContracts.cs）。
// 约定：*Utc / *At 字段是带 Z 的 UTC ISO 字符串；日期（date）字段是 yyyy-MM-dd；响应里没有对象键、COS 地址、腾讯云凭据、PersonId、FaceId。

export interface FaceMatchPermissionFlags {
  manage: boolean
  trafficView: boolean
  unwelcomeManage: boolean
  unwelcomeApprove: boolean
  unwelcomeReview: boolean
  /** 持有 FaceCapture.Images.View：可看「抓拍与比对」页签。老后端没有这个字段，归一化为 false。 */
  resultsView: boolean
}

export interface FaceMatchConfig {
  enabled: boolean
  /** region-missing / region-not-allowed / credentials-missing / credentials-invalid（enabled=false 时才有）。 */
  disabledReason: string | null
  region: string | null
  allowedRegions: string[]
  objectStoreConfigured: boolean
  /** 「较高」档位的相似度下限（0-100）；页面只展示，不自己用它判档。 */
  highBandScore: number
  captureRetentionDays: number
  permissions: FaceMatchPermissionFlags
}

// ───────────── 门店档案与闸门 ─────────────

export interface FaceGateFacet {
  /** capture / match / staffExclusion / trafficDedup / unwelcome */
  facet: string
  requested: boolean
  /**
   * 仍然阻塞保存的缺项（结构性 / 功能性前提，缺了 PUT 返回 409）：allowedSubjects / evaluateMatchRequiresCapture / modeOff / activeEnrollment。
   * 老后端也可能把合规记录键放在这里，页面照常按阻塞展示。
   */
  missing: string[]
  /**
   * 不阻塞保存、但必须始终可见的合规记录缺口（2026-10-13 起 Match 的合规闸门改为警告）：
   * 字段键（小驼峰，与请求体同名）或特殊键 regionMismatch / staffNoticeTooRecent。老后端没有这个字段，归一化为空数组。
   */
  warnings: string[]
}

export interface FaceGateReport {
  facets: FaceGateFacet[]
  /** 只看 missing（阻塞项）；警告不影响。 */
  canSave: boolean
  /** 任一 facet 有警告。老后端没有这个字段，归一化为 false。 */
  hasWarnings: boolean
}

/** 档案可编辑字段（PUT /profiles/{storeCode} 与 POST .../check 共用的请求体）。 */
export interface FaceStoreProfileUpdateRequest {
  /** 0 Off、1 Capture、2 Match */
  mode: number | null
  evaluateMatch: boolean
  staffExclusionEnabled: boolean
  trafficDedupEnabled: boolean
  unwelcomeListEnabled: boolean
  allowedSubjects: string | null
  piaReference: string | null
  signageInstalledOn: string | null
  staffNoticeDate: string | null
  legalSignoffBy: string | null
  legalSignoffAt: string | null
  dedupLegalSignoffBy: string | null
  dedupLegalSignoffAt: string | null
  crossBorderRegion: string | null
  crossBorderReviewRef: string | null
  crossBorderReviewedBy: string | null
  crossBorderReviewedAt: string | null
  timeZoneId: string | null
  unwelcomeAssessmentRef: string | null
  unwelcomeLegalSignoffBy: string | null
  unwelcomeLegalSignoffAt: string | null
  staffTrainingDate: string | null
  privacyContact: string | null
  /**
   * 命中任务自动确认阈值（相似度 0-100）：达到或超过时系统自动把任务标为「已确认」，不再需要人工复核。
   * 0 = 不自动确认；50–100（最多 2 位小数）= 阈值。Web 保存时始终带上数字；
   * null = 保持库里原值（后端语义，旧版 App 的保存不带这个字段；Web 只在输入当前不合法、仅做闸门检查时才会是 null，保存前一定先校验）。
   */
  unwelcomeAutoConfirmScore: number | null
}

export interface FaceStoreProfile extends FaceStoreProfileUpdateRequest {
  /** 生效值：0 = 不自动确认；没有档案行或未设置时为默认 90。 */
  unwelcomeAutoConfirmScore: number
  storeCode: string
  storeName: string | null
  /** 档案行是否已存在；不存在时其余字段是默认值。 */
  exists: boolean
  updatedAtUtc: string | null
  updatedBy: string | null
  effectiveTimeZoneId: string
  /** NSW / QLD / null */
  jurisdiction: string | null
  /** 已保存的跨境审查地域与当前配置地域一致。 */
  crossBorderReviewValid: boolean
  gates: FaceGateReport
}

export interface FaceStoreProfileListItem {
  storeCode: string
  storeName: string | null
  mode: number
  evaluateMatch: boolean
  staffExclusionEnabled: boolean
  trafficDedupEnabled: boolean
  unwelcomeListEnabled: boolean
  crossBorderReviewValid: boolean
  /** 合规记录警告条数（各 facet 警告之和）；> 0 时列表打红色「合规记录未齐」标记。老后端没有这个字段，归一化为 0。 */
  gateWarningCount: number
  updatedAtUtc: string | null
}

// ───────────── 员工同意与登记 ─────────────

export interface FaceStaffCandidate {
  userGuid: string
  displayName: string
  email: string | null
  /** none / active */
  enrollmentState: string
  requiresNswNotice: boolean
}

export type FaceConsentMethod = 'paper' | 'electronic'

export interface FaceConsent {
  consentId: string
  userGuid: string
  userName: string | null
  consentVersion: string
  signedAtUtc: string
  method: string
  hasEvidence: boolean
  nswNoticeDate: string | null
  withdrawnAtUtc: string | null
  withdrawnReason: string | null
  createdAtUtc: string
  createdBy: string
  active: boolean
}

export interface FaceConsentWithdrawResult {
  consent: FaceConsent
  withdrawnEnrollmentCount: number
  pendingRemoteDeletionCount: number
}

export interface FaceEnrollment {
  enrollmentId: string
  userGuid: string
  userName: string | null
  consentId: string
  photoCount: number
  /** 1 Active、2 Withdrawn */
  status: number
  statusName: string
  enrolledAtUtc: string
  enrolledBy: string
  withdrawnAtUtc: string | null
  /** 登记仍有效但同意已撤回：腾讯云侧删除待完成，后台任务会重试。 */
  pendingRemoteDeletion: boolean
}

/** 记录同意的入参；scan 以 multipart 字段 `scan` 上传。 */
export interface CreateFaceConsentInput {
  userGuid: string
  consentVersion: string
  signedAtUtc: string
  method: FaceConsentMethod
  nswNoticeDate: string | null
  scan: File | null
}

/** 登记人脸的入参；photos 以 multipart 重复字段 `photos` 上传。 */
export interface CreateFaceEnrollmentInput {
  userGuid: string
  consentId: string
  photos: File[]
}

// ───────────── 客流统计与审计 ─────────────

export interface FaceTrafficCounts {
  captureCount: number
  staffMatchCount: number
  /** 估算值：摄像机角度、距离、遮挡会造成同一人被认成两人（偏高）或两人认成一人（偏低）。 */
  uniqueVisitorCount: number
  repeatCount: number
  lowQualityCount: number
  apiFailureCount: number
}

export interface FaceTrafficDay {
  date: string
  counts: FaceTrafficCounts
}

export interface FaceTrafficHour {
  hour: number
  counts: FaceTrafficCounts
}

export interface FaceTraffic {
  storeCode: string
  storeName: string | null
  timeZoneId: string
  from: string
  to: string
  days: FaceTrafficDay[]
  /** 仅单日查询时有值。 */
  hours: FaceTrafficHour[]
  total: FaceTrafficCounts
  uniqueVisitorIsEstimate: boolean
}

export interface FaceAuditItem {
  auditId: number
  atUtc: string
  actorId: string
  actorName: string
  action: string
  storeCode: string | null
  targetRef: string | null
  result: string
}

export interface FaceMatchPage<T> {
  items: T[]
  page: number
  pageSize: number
  hasMore: boolean
}

// ───────────── 不受欢迎人员名单 ─────────────

/** 1 暴力或威胁、2 有组织或有记录的反复盗窃、3 其他（须说明；非管理员提交需两位批准人，管理员一人即可）。没有「一般偷窃」。 */
export type FaceUnwelcomeCategory = 1 | 2 | 3

/** 0 待批准、1 生效中、2 已到期、3 已撤回。 */
export type FaceUnwelcomeEntryStatus = 0 | 1 | 2 | 3

/** 0 待复核、1 已驳回（判断「不是同一个人」）、2 已确认（判断「是同一个人」）。 */
export type FaceUnwelcomeReviewStatus = 0 | 1 | 2

export interface FaceUnwelcomeEntry {
  entryId: string
  incidentCategory: number
  incidentCategoryName: string
  categoryNote: string | null
  evidenceRef: string
  storeScope: string[]
  submittedById: string
  submittedByName: string
  submittedAtUtc: string
  approvedByName: string | null
  approvedAtUtc: string | null
  secondApprovedByName: string | null
  secondApprovedAtUtc: string | null
  approvalsNeeded: number
  approvalsDone: number
  reviewDueAtUtc: string
  expiresAtUtc: string
  status: number
  statusName: string
  photoCount: number
  disputeNote: string | null
  expiringSoon: boolean
  daysToExpiry: number
  canApprove: boolean
  /** 已撤回 / 已过期，但腾讯云人员或参考照片还没删除干净（后台会继续清理）。 */
  cleanupPending: boolean
}

/** 提交条目的入参；photos 以 multipart 重复字段 `photos` 上传，storeScope 以逗号拼接。 */
export interface CreateFaceUnwelcomeEntryInput {
  incidentCategory: number
  categoryNote: string | null
  evidenceRef: string
  storeScope: string[]
  expiresAtUtc: string
  photos: File[]
}

export type FaceUnwelcomeBand = 'high' | 'normal'

export interface FaceUnwelcomeTask {
  taskId: string
  storeCode: string
  storeName: string | null
  cameraId: string
  capturedAtUtc: string
  entryId: string
  /** 相似度 0-100，不等于是同一个人的概率。 */
  score: number
  scorePercent: number
  band: FaceUnwelcomeBand
  reviewStatus: number
  reviewStatusName: string
  firstReviewerName: string | null
  firstReviewedAtUtc: string | null
  /** 只有旧版遗留任务才有；展示时当作「补充复核人」。 */
  secondReviewerName: string | null
  secondReviewedAtUtc: string | null
  imageAvailable: boolean
  imageDeleteAtUtc: string
  referencePhotoCount: number
  /** first = 待复核；none = 已结束。旧版后端还会返回 second（已一审待二审的遗留任务），前端一律当作待复核。 */
  nextStep: string
  canReview: boolean
  /**
   * true = 相似度达到门店阈值，系统自动确认的任务：此时 reviewStatusName 为 confirmed、firstReviewerName 为 null、canReview 为 false。
   * 老后端没有这个字段，归一化为 false。
   */
  autoConfirmed: boolean
  /**
   * 命中通知（经通知平台）发送状态：sent 已发布给收件人 / pending 排队中（暂无收件人会重试）/ skipped 发布前已被复核或图片到期而不再发。
   * 后端可能新增取值，页面按未知值兜底；老后端没有这个字段，归一化为 pending。
   */
  notifyState: string
  notifiedAtUtc: string | null
  /** 收件人数（不含姓名）；未发布为 0。 */
  notifyRecipientCount: number
  notifyReadCount: number
}

export type FaceReviewVerdict = 'match' | 'notMatch'

export interface FaceUnwelcomeStatsEntry {
  entryId: string
  statusName: string
  taskCount: number
  pendingCount: number
  dismissedCount: number
  /** 只含人工确认的任务（系统自动确认的单独计入 autoConfirmedCount）。 */
  confirmedCount: number
  /** 系统自动确认的任务数；老后端没有这个字段，归一化为 0。不参与驳回率和「建议复核」。 */
  autoConfirmedCount: number
  /** 驳回率 0-1 = 已驳回 ÷（已驳回 + 人工确认）；没有人工结案的任务时为 null。 */
  dismissRate: number | null
  suggestReview: boolean
}

export interface FaceUnwelcomeStatsBucket {
  from: number
  to: number
  total: number
  dismissedCount: number
  /** 只含人工确认的任务。 */
  confirmedCount: number
  /** 系统自动确认的任务数；老后端没有这个字段，归一化为 0。 */
  autoConfirmedCount: number
}

export interface FaceUnwelcomeStats {
  taskCount: number
  pendingCount: number
  dismissedCount: number
  /** 只含人工确认的任务。taskCount = pendingCount + dismissedCount + confirmedCount + autoConfirmedCount。 */
  confirmedCount: number
  /** 系统自动确认的任务数；老后端没有这个字段，归一化为 0。 */
  autoConfirmedCount: number
  /** 驳回率 = 已驳回 ÷（已驳回 + 人工确认）；系统自动确认的不计入；没有人工结案的任务时为 null。 */
  dismissRate: number | null
  entries: FaceUnwelcomeStatsEntry[]
  scoreBuckets: FaceUnwelcomeStatsBucket[]
}

// ───────────── 抓拍与比对结果（FaceCapture.Images.View） ─────────────

/** 抓拍对应的名单命中任务摘要（不含姓名与事件细节）。 */
export interface FaceCaptureResultHit {
  taskId: string
  /** 相似度 0-100，不等于是同一个人的概率。 */
  scorePercent: number
  /** 档位由后端给出时才有（当前接口没有这个字段，页面不自己用阈值划档）。 */
  band: FaceUnwelcomeBand | null
  reviewStatus: number
  reviewStatusName: string
  /** 命中图是否还在（到期删除后为 false，不能再去复核）。 */
  imageAvailable: boolean
  notifyState: string
  notifiedAtUtc: string | null
  notifyRecipientCount: number
  notifyReadCount: number
}

/**
 * 一次抓拍及其比对结果。分数是相似度（0-100），不是「同一个人的概率」。
 * decision 保留后端原始取值（staff / repeatVisitor / newVisitor / lowQuality / noMatch），未评估为 null；页面遇到未知取值要能展示。
 * faceImageId / sceneImageId 只是图片编号（不是地址）：图片字节必须点「查看」后才按需读取并写审计。
 */
export interface FaceCaptureResult {
  captureId: string
  storeCode: string
  storeName: string | null
  cameraId: string
  capturedAtUtc: string
  faceCount: number
  isDuplicate: boolean
  evaluated: boolean
  decision: string | null
  staffScorePercent: number | null
  visitScorePercent: number | null
  faceImageId: string | null
  sceneImageId: string | null
  hit: FaceCaptureResultHit | null
}

/** 命中通知的一位收件人：只有展示名与已读状态，没有账号标识（用户编号、用户名、邮箱）。 */
export interface FaceHitRecipient {
  /** 全名；后端已按「全名 → 用户名 → -」回退，这里再兜底成 -。 */
  displayName: string
  read: boolean
  /** 已读时间（UTC）；未读为 null。 */
  readAtUtc: string | null
}

/**
 * 抓拍详情抽屉的补充信息（GET /results/hits/{taskId}/detail，需 FaceCapture.Images.View）。
 * 单独一个接口，不加进命中摘要 FaceCaptureResultHit：那份字段集合是跨端契约。
 * targetPhotoCount 是名单条目参考照片的张数（条目已撤回 / 过期 / 照片已清空为 0）；图片本身仍走复核任务的参考照片接口。
 */
export interface FaceHitDetail {
  taskId: string
  targetPhotoCount: number
  notificationRevoked: boolean
  /** 没有通知行（还没发）时为空数组；后端已按未读在前、展示名排序。 */
  recipients: FaceHitRecipient[]
}

/** GET /results 的查询条件；fromUtc 含、toUtc 不含；pageSize ≤ 50。 */
export interface FaceCaptureResultQuery {
  storeCode?: string
  cameraId?: string
  decision?: string
  hitOnly?: boolean
  fromUtc?: string
  toUtc?: string
  page: number
  pageSize: number
}
