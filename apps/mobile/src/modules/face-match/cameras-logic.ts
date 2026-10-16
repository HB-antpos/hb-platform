// 摄像头区的纯逻辑：推送状态判定、按门店过滤、启停 / 新增 / 编辑请求体构造、表单校验、最近抓拍的展示与分页规则。
// 字段规则与网页后台、后端一致（编号 ^[a-z0-9][a-z0-9-]{0,63}$ 等）；新增 / 重新生成令牌后的一次性地址见 camera-secret.ts。
import type {
  CreateFaceCameraRequest,
  FaceCamera,
  FaceCameraCapture,
  FaceCameraCaptureImage,
  FaceCameraParseStatus,
  UpdateFaceCameraRequest,
} from "./types";

/**
 * 摄像头的「推送状态」。注意不是在线 / 离线：海康摄像机在推送模式下只有检测到人脸才推送，
 * 没人经过镜头时什么都不发、也没有心跳，所以「暂无推送」不等于断电或断网，文案和命名都不能写成「离线」。
 * 后端字段仍叫 online（契约不改名），只在展示层换成不会误导的说法。
 */
export type CameraStatus = "disabled" | "recentlyActive" | "noRecentPush";

/** 停用优先于推送状态：已停用的摄像头即使仍有推送也显示「已停用」；其余按服务端算好的 online 判定。 */
export function getCameraStatus(camera: Pick<FaceCamera, "enabled" | "online">): CameraStatus {
  if (!camera.enabled) return "disabled";
  return camera.online ? "recentlyActive" : "noRecentPush";
}

export const CAMERA_STATUS_LABEL_KEYS: Record<CameraStatus, string> = {
  recentlyActive: "settings.cameras.status.recentlyActive",
  noRecentPush: "settings.cameras.status.noRecentPush",
  disabled: "settings.cameras.status.disabled",
};

/** 摄像头列表接口返回全部门店的摄像头，按所选门店在本地过滤；列表顺序：先按编号稳定排序。 */
export function filterCamerasByStore(cameras: readonly FaceCamera[], storeCode: string | null): FaceCamera[] {
  if (!storeCode) return [];
  return cameras
    .filter((camera) => camera.storeCode === storeCode)
    .sort((a, b) => a.cameraId.localeCompare(b.cameraId));
}

/** 显示名称：有备注用备注，否则用摄像头编号。 */
export function cameraDisplayName(camera: Pick<FaceCamera, "cameraId" | "note">): string {
  return camera.note?.trim() || camera.cameraId;
}

/**
 * 启停请求体。PUT 是整体替换：门店、备注、型号、MAC 必须原样带回服务端返回的值，只改 enabled，
 * 否则未带的字段会被清空。
 */
export function buildCameraEnabledRequest(
  camera: Pick<FaceCamera, "storeCode" | "note" | "deviceModel" | "macAddress">,
  enabled: boolean,
): UpdateFaceCameraRequest {
  return {
    storeCode: camera.storeCode,
    note: camera.note,
    deviceModel: camera.deviceModel,
    macAddress: camera.macAddress,
    enabled,
  };
}

/** 近 24 小时解析失败数大于 0 时用警示色。 */
export function hasParseFailures(camera: Pick<FaceCamera, "parseFailureCount24h">): boolean {
  return camera.parseFailureCount24h > 0;
}

export interface CameraStatusCounts {
  total: number;
  recentlyActive: number;
  noRecentPush: number;
  disabled: number;
}

export function countCameraStatuses(cameras: readonly Pick<FaceCamera, "enabled" | "online">[]): CameraStatusCounts {
  const counts: CameraStatusCounts = { total: cameras.length, recentlyActive: 0, noRecentPush: 0, disabled: 0 };
  for (const camera of cameras) counts[getCameraStatus(camera)] += 1;
  return counts;
}

// ───────────── 表单字段规则（与后端、网页后台一致） ─────────────

/** 摄像头编号：小写字母数字横线，不以横线开头，最长 64（后端 ^[a-z0-9][a-z0-9-]{0,63}\z）；创建后不可修改。 */
export const CAMERA_ID_PATTERN = /^[a-z0-9][a-z0-9-]{0,63}$/;
export const CAMERA_ID_MAX_LENGTH = 64;
/** 门店编码只允许字母数字下划线横线。 */
export const CAMERA_STORE_CODE_PATTERN = /^[A-Za-z0-9_-]+$/;
export const CAMERA_STORE_CODE_MAX_LENGTH = 32;
export const CAMERA_NOTE_MAX_LENGTH = 200;
export const CAMERA_DEVICE_MODEL_MAX_LENGTH = 64;
export const CAMERA_MAC_ADDRESS_MAX_LENGTH = 32;

export interface CameraFormValues {
  cameraId: string;
  storeCode: string;
  note: string;
  deviceModel: string;
  macAddress: string;
}

export const EMPTY_CAMERA_FORM: CameraFormValues = { cameraId: "", storeCode: "", note: "", deviceModel: "", macAddress: "" };

/** 去首尾空白；空串（含全空白）规范成 null，后端把 null 当作「未填写」。 */
export function normalizeOptionalText(value: string | null | undefined): string | null {
  const trimmed = (value ?? "").trim();
  return trimmed ? trimmed : null;
}

/** 校验结果的值是文案键（faceMatch 命名空间）；空对象表示通过。 */
export type CameraFormErrors = Partial<Record<keyof CameraFormValues, string>>;

/** 整表校验；编辑模式不校验 cameraId（不可改）。 */
export function validateCameraForm(values: CameraFormValues, mode: "create" | "edit"): CameraFormErrors {
  const errors: CameraFormErrors = {};
  if (mode === "create") {
    const cameraId = values.cameraId.trim();
    if (!cameraId) errors.cameraId = "settings.cameras.validation.cameraIdRequired";
    else if (!CAMERA_ID_PATTERN.test(cameraId)) errors.cameraId = "settings.cameras.validation.cameraIdInvalid";
  }
  const storeCode = values.storeCode.trim();
  if (!storeCode) errors.storeCode = "settings.cameras.validation.storeCodeRequired";
  else if (storeCode.length > CAMERA_STORE_CODE_MAX_LENGTH) errors.storeCode = "settings.cameras.validation.storeCodeTooLong";
  else if (!CAMERA_STORE_CODE_PATTERN.test(storeCode)) errors.storeCode = "settings.cameras.validation.storeCodeInvalid";
  if (values.note.trim().length > CAMERA_NOTE_MAX_LENGTH) errors.note = "settings.cameras.validation.noteTooLong";
  if (values.deviceModel.trim().length > CAMERA_DEVICE_MODEL_MAX_LENGTH) errors.deviceModel = "settings.cameras.validation.deviceModelTooLong";
  if (values.macAddress.trim().length > CAMERA_MAC_ADDRESS_MAX_LENGTH) errors.macAddress = "settings.cameras.validation.macAddressTooLong";
  return errors;
}

function buildCameraFields(values: CameraFormValues) {
  return {
    storeCode: values.storeCode.trim(),
    note: normalizeOptionalText(values.note),
    deviceModel: normalizeOptionalText(values.deviceModel),
    macAddress: normalizeOptionalText(values.macAddress),
  };
}

export function buildCreateCameraRequest(values: CameraFormValues): CreateFaceCameraRequest {
  return { cameraId: values.cameraId.trim(), ...buildCameraFields(values) };
}

/** 编辑请求体：PUT 是整体替换，enabled 沿用摄像头当前值（启停只走启停开关）。 */
export function buildEditCameraRequest(values: CameraFormValues, enabled: boolean): UpdateFaceCameraRequest {
  return { ...buildCameraFields(values), enabled };
}

/** 编辑表单的初始值：null 展示成空串。 */
export function toCameraFormValues(camera: Pick<FaceCamera, "cameraId" | "storeCode" | "note" | "deviceModel" | "macAddress">): CameraFormValues {
  return {
    cameraId: camera.cameraId,
    storeCode: camera.storeCode,
    note: camera.note ?? "",
    deviceModel: camera.deviceModel ?? "",
    macAddress: camera.macAddress ?? "",
  };
}

/** 编辑表单是否有改动（没改就不发请求）。 */
export function isCameraFormChanged(initial: CameraFormValues, current: CameraFormValues): boolean {
  return (
    initial.storeCode.trim() !== current.storeCode.trim() ||
    (normalizeOptionalText(initial.note) ?? "") !== (normalizeOptionalText(current.note) ?? "") ||
    (normalizeOptionalText(initial.deviceModel) ?? "") !== (normalizeOptionalText(current.deviceModel) ?? "") ||
    (normalizeOptionalText(initial.macAddress) ?? "") !== (normalizeOptionalText(current.macAddress) ?? "")
  );
}

// ───────────── 最近抓拍：分页与展示 ─────────────

/** 单页 20 条（后端最大 50），页码最大 100：到顶后不再提供「加载更多」。 */
export const CAMERA_CAPTURE_PAGE_SIZE = 20;
export const CAMERA_CAPTURE_MAX_PAGE = 100;

/** 下一页页码；没有更多或到达页码上限返回 undefined（供 useInfiniteQuery 的 getNextPageParam 使用）。 */
export function nextCapturePage(page: { page: number; hasMore: boolean }): number | undefined {
  return page.hasMore && page.page < CAMERA_CAPTURE_MAX_PAGE ? page.page + 1 : undefined;
}

export const CAMERA_PARSE_STATUS_LABEL_KEYS: Record<FaceCameraParseStatus, string> = {
  0: "settings.cameras.captures.parse.parsed",
  1: "settings.cameras.captures.parse.partial",
  2: "settings.cameras.captures.parse.unparsed",
  3: "settings.cameras.captures.parse.notFace",
};

/** 解析状态文案键；服务端新增取值（null）显示「未知」，不假装成某一种。 */
export function cameraParseStatusLabelKey(status: FaceCameraParseStatus | null): string {
  return status === null ? "settings.cameras.captures.parse.unknown" : CAMERA_PARSE_STATUS_LABEL_KEYS[status];
}

/** 解析状态的色调：已解析绿、部分解析橙、未解析红、非人脸事件灰、未知灰。 */
export function cameraParseStatusTone(status: FaceCameraParseStatus | null): "success" | "warning" | "danger" | "neutral" {
  if (status === 0) return "success";
  if (status === 1) return "warning";
  if (status === 2) return "danger";
  return "neutral";
}

/** 图片还在接收服务本地磁盘（storage=local）时后台读不到，不提供「查看」入口。 */
export function isCameraImageViewable(image: Pick<FaceCameraCaptureImage, "storage">): boolean {
  return image.storage.toLowerCase() !== "local";
}

/** 这条抓拍有没有可查看的图片（用于决定是否显示「查看图片」按钮）。 */
export function hasViewableImages(capture: Pick<FaceCameraCapture, "images">): boolean {
  return capture.images.some(isCameraImageViewable);
}

/** 图片按钮的文案键与参数：人脸图带序号（faceIndex 从 0 起，显示从 1 起），背景图 / 其他各一个。 */
export function cameraImageLabel(image: Pick<FaceCameraCaptureImage, "kind" | "faceIndex">, ordinal: number): { key: string; values: { index: number } } {
  if (image.kind === 0) {
    return { key: "settings.cameras.captures.imageFace", values: { index: (image.faceIndex ?? ordinal) + 1 } };
  }
  if (image.kind === 1) return { key: "settings.cameras.captures.imageBackground", values: { index: ordinal + 1 } };
  return { key: "settings.cameras.captures.imageOther", values: { index: ordinal + 1 } };
}
