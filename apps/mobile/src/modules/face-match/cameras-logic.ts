// 摄像头区的纯逻辑：推送状态判定、按门店过滤、启停请求体构造。
// 只做查看与启停：不新建摄像头、不重置令牌、不展示 / 修改令牌（安全原因；这些只在网页后台做）。
import type { FaceCamera, UpdateFaceCameraRequest } from "./types";

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
