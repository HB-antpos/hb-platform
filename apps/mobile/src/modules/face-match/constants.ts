import { PERMISSIONS } from "@/shared/utils/access";

/** apiClient 的 baseURL 已带 /api，业务路径与其他 mobile API 一致，不再加 /api 前缀。 */
export const FACE_MATCH_API_BASE = "/react/v1/face-match";
export const FACE_CAMERAS_API_BASE = "/react/v1/face-cameras";

/**
 * 后端 Permissions.FaceMatch.* / FaceCapture.Cameras.Manage（本期不新增权限码）。
 * 入口可见性只用这些码；页内按钮以 GET /face-match/config 返回的权限标志为准。
 */
export const FACE_MATCH_PERMISSIONS = {
  manage: PERMISSIONS.FaceMatch.Manage,
  unwelcomeManage: PERMISSIONS.FaceMatch.Unwelcome.Manage,
  unwelcomeApprove: PERMISSIONS.FaceMatch.Unwelcome.Approve,
  unwelcomeReview: PERMISSIONS.FaceMatch.Unwelcome.Review,
  camerasManage: PERMISSIONS.FaceCapture.CamerasManage,
  imagesView: PERMISSIONS.FaceCapture.ImagesView,
} as const;

// ───────────── 字段与上传规则（与后端、Web 一致） ─────────────

export const MB = 1024 * 1024;
/** 参考照片：JPG / PNG，单张 ≤ 5MB，1–5 张。 */
export const PHOTO_MAX_BYTES = 5 * MB;
export const UNWELCOME_PHOTO_MIN = 1;
export const UNWELCOME_PHOTO_MAX = 5;
/** 名单条目到期日上限：今天起 12 个月。 */
export const UNWELCOME_MAX_EXPIRY_MONTHS = 12;
export const CATEGORY_NOTE_MAX_LENGTH = 200;
export const EVIDENCE_REF_MAX_LENGTH = 100;
export const DISPUTE_NOTE_MAX_LENGTH = 500;
export const REASON_MAX_LENGTH = 200;
export const ALLOWED_SUBJECTS_MAX_LENGTH = 200;
export const PAGE_SIZE = 20;

export const CATEGORY_VIOLENCE = 1;
export const CATEGORY_ORGANISED_THEFT = 2;
export const CATEGORY_OTHER = 3;

export const ENTRY_STATUS_PENDING = 0;
export const ENTRY_STATUS_ACTIVE = 1;
export const ENTRY_STATUS_EXPIRED = 2;
export const ENTRY_STATUS_WITHDRAWN = 3;

export const REVIEW_STATUS_PENDING = 0;
export const REVIEW_STATUS_DISMISSED = 1;
export const REVIEW_STATUS_CONFIRMED = 2;

export const PROFILE_MODE_OFF = 0;
export const PROFILE_MODE_CAPTURE = 1;
export const PROFILE_MODE_MATCH = 2;

/** 后端允许的门店时区（StoreTimeZonePolicy）；空 = 自动判断。 */
export const PROFILE_TIME_ZONES = ["Australia/Sydney", "Australia/Brisbane", "Australia/Melbourne"] as const;
