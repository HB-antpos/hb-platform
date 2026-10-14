// 人脸比对（不受欢迎人员名单、门店档案、摄像头）移动端类型。
// 契约来自后端 /api/react/v1/face-match 与 /api/react/v1/face-cameras，与 Web 的 types/faceMatch.ts、types/faceCamera.ts 保持字段一致。
// 约定：*Utc 字段是带 Z 的 UTC ISO 字符串；日期字段是 yyyy-MM-dd；响应里没有对象键、COS 地址、腾讯云凭据、PersonId、FaceId。

export interface FaceMatchPermissionFlags {
  manage: boolean;
  trafficView: boolean;
  unwelcomeManage: boolean;
  unwelcomeApprove: boolean;
  unwelcomeReview: boolean;
}

export interface FaceMatchConfig {
  enabled: boolean;
  /** region-missing / region-not-allowed / credentials-missing / credentials-invalid（enabled=false 时才有）。 */
  disabledReason: string | null;
  region: string | null;
  allowedRegions: string[];
  objectStoreConfigured: boolean;
  /** 「较高」档位的相似度下限（0-100）；页面只展示，不自己用它判档。 */
  highBandScore: number;
  captureRetentionDays: number;
  permissions: FaceMatchPermissionFlags;
}

// ───────────── 门店档案与闸门 ─────────────

export interface FaceGateFacet {
  /** capture / match / staffExclusion / trafficDedup / unwelcome */
  facet: string;
  requested: boolean;
  /** 仍然阻塞保存的缺项（缺了 PUT 返回 409）。 */
  missing: string[];
  /** 不阻塞保存、但必须始终可见的合规记录缺口（字段键，或 regionMismatch / staffNoticeTooRecent）。 */
  warnings: string[];
}

export interface FaceGateReport {
  facets: FaceGateFacet[];
  /** 只看 missing（阻塞项）；警告不影响。 */
  canSave: boolean;
  hasWarnings: boolean;
}

/**
 * 档案可编辑字段（PUT /profiles/{storeCode} 与 POST .../check 共用的请求体）。
 * 注意：PUT 是整体替换——没出现在请求里的字段会被清空，所以手机不编辑的字段必须原样带回服务端返回的值。
 */
export interface FaceStoreProfileUpdateRequest {
  /** 0 Off、1 Capture、2 Match */
  mode: number | null;
  evaluateMatch: boolean;
  staffExclusionEnabled: boolean;
  trafficDedupEnabled: boolean;
  unwelcomeListEnabled: boolean;
  allowedSubjects: string | null;
  piaReference: string | null;
  signageInstalledOn: string | null;
  staffNoticeDate: string | null;
  legalSignoffBy: string | null;
  legalSignoffAt: string | null;
  dedupLegalSignoffBy: string | null;
  dedupLegalSignoffAt: string | null;
  crossBorderRegion: string | null;
  crossBorderReviewRef: string | null;
  crossBorderReviewedBy: string | null;
  crossBorderReviewedAt: string | null;
  timeZoneId: string | null;
  unwelcomeAssessmentRef: string | null;
  unwelcomeLegalSignoffBy: string | null;
  unwelcomeLegalSignoffAt: string | null;
  staffTrainingDate: string | null;
  privacyContact: string | null;
}

export interface FaceStoreProfile extends FaceStoreProfileUpdateRequest {
  storeCode: string;
  storeName: string | null;
  /** 档案行是否已存在；不存在时其余字段是默认值。 */
  exists: boolean;
  updatedAtUtc: string | null;
  updatedBy: string | null;
  effectiveTimeZoneId: string;
  /** NSW / QLD / null */
  jurisdiction: string | null;
  crossBorderReviewValid: boolean;
  gates: FaceGateReport;
}

// ───────────── 不受欢迎人员名单 ─────────────

/** 1 暴力或威胁、2 有组织或有记录的反复盗窃、3 其他（须说明）。没有「一般偷窃」。 */
export type FaceUnwelcomeCategory = 1 | 2 | 3;

export interface FaceMatchPage<T> {
  items: T[];
  page: number;
  pageSize: number;
  hasMore: boolean;
}

export interface FaceUnwelcomeEntry {
  entryId: string;
  incidentCategory: number;
  incidentCategoryName: string;
  categoryNote: string | null;
  evidenceRef: string;
  storeScope: string[];
  submittedById: string;
  submittedByName: string;
  submittedAtUtc: string;
  approvedByName: string | null;
  approvedAtUtc: string | null;
  secondApprovedByName: string | null;
  secondApprovedAtUtc: string | null;
  approvalsNeeded: number;
  approvalsDone: number;
  reviewDueAtUtc: string;
  expiresAtUtc: string;
  /** 0 待批准、1 生效中、2 已到期、3 已撤回 */
  status: number;
  statusName: string;
  photoCount: number;
  disputeNote: string | null;
  expiringSoon: boolean;
  daysToExpiry: number;
  canApprove: boolean;
  /** 已撤回 / 已过期，但腾讯云人员或参考照片还没删除干净（后台会继续清理）。 */
  cleanupPending: boolean;
}

export type FaceUnwelcomeBand = "high" | "normal";

export interface FaceUnwelcomeTask {
  taskId: string;
  storeCode: string;
  storeName: string | null;
  cameraId: string;
  capturedAtUtc: string;
  entryId: string;
  /** 相似度 0-100，不等于「是同一个人的概率」。 */
  score: number;
  scorePercent: number;
  band: FaceUnwelcomeBand;
  /** 0 待复核、1 已驳回（判断「不是同一个人」）、2 已确认（判断「是同一个人」） */
  reviewStatus: number;
  reviewStatusName: string;
  /** 复核人（新规则下一人判断即结束，只有这一位）。 */
  firstReviewerName: string | null;
  firstReviewedAtUtc: string | null;
  /** 仅旧版遗留的、已有两位复核人的任务才有，展示为「补充复核人」。 */
  secondReviewerName: string | null;
  secondReviewedAtUtc: string | null;
  imageAvailable: boolean;
  imageDeleteAtUtc: string;
  referencePhotoCount: number;
  /** first（待复核）/ none（已结束）；旧后端还可能返回 second（旧版遗留的待二审），一律按待复核处理。 */
  nextStep: string;
  canReview: boolean;
}

export type FaceReviewVerdict = "match" | "notMatch";

/** 列表查询：status 为数值（条目 0-3、任务 0-2），不传为全部。 */
export interface FaceUnwelcomeListQuery {
  status?: number;
  storeCode?: string;
  page: number;
  pageSize: number;
}

// ───────────── 摄像头 ─────────────

export interface FaceCamera {
  cameraId: string;
  storeCode: string;
  storeName: string | null;
  deviceModel: string | null;
  macAddress: string | null;
  note: string | null;
  enabled: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
  lastSeenAtUtc: string | null;
  /** 服务端已算好：启用且 onlineWindowMinutes 内有推送。界面不写成在线 / 离线（摄像机没有心跳）。 */
  online: boolean;
  captureCount24h: number;
  faceCount24h: number;
  parseFailureCount24h: number;
  lastEventAtUtc: string | null;
}

export interface FaceCameraList {
  items: FaceCamera[];
  generatedAtUtc: string;
  onlineWindowMinutes: number;
  statsWindowHours: number;
}

/** PUT /face-cameras/{id} 是整体替换：除 enabled 外的字段必须原样带回，否则会被清空。 */
export interface UpdateFaceCameraRequest {
  storeCode: string;
  note: string | null;
  deviceModel: string | null;
  macAddress: string | null;
  enabled: boolean;
}

// ───────────── 抓拍与比对结果（GET /face-match/results） ─────────────

/** 比对判定：服务端取值；新增取值时页面按「未识别的判定」兜底，不丢弃记录。 */
export type FaceCaptureDecision = "staff" | "repeatVisitor" | "newVisitor" | "lowQuality" | "noMatch";

/** 抓拍命中名单后的任务摘要（不含姓名与事件细节）。notifyState：sent / pending / skipped，其余取值页面不展示通知行。 */
export interface FaceCaptureHit {
  taskId: string;
  scorePercent: number;
  band: FaceUnwelcomeBand;
  /** 0 待复核、1 已驳回、2 已确认。 */
  reviewStatus: number;
  reviewStatusName: string;
  /** 命中抓拍图是否还在保留期内（决定能否去复核）。 */
  imageAvailable: boolean;
  notifyState: string;
  notifiedAtUtc: string | null;
  notifyRecipientCount: number;
  notifyReadCount: number;
}

/** 一次抓拍及其比对结果。图片编号只在服务端确认有看图权限时才有；图片内容另走专用接口按需读取。 */
export interface FaceCaptureResult {
  captureId: string;
  storeCode: string;
  storeName: string | null;
  cameraId: string;
  capturedAtUtc: string;
  faceCount: number;
  isDuplicate: boolean;
  /** false = 没有评估行（开关未开、重复抓拍、接口失败），此时 decision 为 null。 */
  evaluated: boolean;
  decision: string | null;
  staffScorePercent: number | null;
  visitScorePercent: number | null;
  faceImageId: string | null;
  sceneImageId: string | null;
  hit: FaceCaptureHit | null;
}

export interface FaceCaptureResultQuery {
  storeCode?: string;
  /** staff / repeatVisitor / newVisitor / lowQuality / noMatch / notEvaluated，空 = 不限。 */
  decision?: string;
  hitOnly?: boolean;
  fromUtc?: string;
  toUtc?: string;
  page: number;
  pageSize: number;
}

// ───────────── 内存中的照片 ─────────────

/**
 * 待提交的参考照片，只存在于组件 state（内存）：
 * base64 是已压缩成 JPEG 的字节，previewDataUri 是缩略图，两者都不写磁盘、不进日志、不进诊断上报。
 */
export interface FacePhotoDraft {
  key: string;
  /** JPEG 字节的 base64（不含 data: 前缀），提交时解码后写入 multipart。 */
  base64: string;
  /** 压缩后 JPEG 字节数。 */
  byteSize: number;
  width: number;
  height: number;
  /** 缩略图 data URI，仅用于预览。 */
  previewDataUri: string;
}
