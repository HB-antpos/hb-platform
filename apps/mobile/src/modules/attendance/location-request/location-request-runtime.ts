// 位置请求待办消费的运行时接线：把纯逻辑（consumer / capture / api-core）与真实依赖
// （登录状态与权限、apiClient、expo-location、设备信息、提示 store）接在一起。
//
// 进程内只有一个消费器实例，所有触发点（根布局 hook、推送运行时、通知收件箱）共用它，
// 才能共享 in-flight 锁、15 秒节流与「会话内已处理」去重。
//
// 取位置只走两条路：
// - 非交互：getForegroundPermissionsAsync（只读查询，不弹框）→ 已授予才 getCurrentPositionAsync（Accuracy.High）；
// - 交互（点推送 / 点收件箱通知）：required-location.ts 的 collectRequiredLocation（含「使用 App 期间」权限申请）。
// 绝不请求后台权限、不启动任何后台任务。
import * as Location from "expo-location";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import { collectRequiredLocation, getAttendanceDeviceContext } from "@/modules/attendance/required-location";
import { useAuthStore } from "@/store/auth-store";
import { PERMISSIONS } from "@/shared/utils/access";
import { locationRequestApi } from "./location-request-api";
import { captureLocationForRequest, type LocationRequestCaptureBridge } from "./location-request-capture";
import { createLocationRequestConsumer } from "./location-request-consumer";
import {
  resolveLocationRequestUserGuid,
  type LocationRequestSessionSnapshot,
  type LocationRequestTrigger,
} from "./location-request-logic";
import { useLocationRequestNoticeStore } from "./location-request-notice-store";

/** 从登录状态读出资格快照：个人账号会话 + 持有 Attendance.Punch.Self + 非 iOS 审核态。 */
function readSessionSnapshot(): LocationRequestSessionSnapshot {
  const auth = useAuthStore.getState();
  return {
    isAuthenticated: auth.isAuthenticated,
    sessionKind: auth.sessionKind,
    iosReview: isIosReviewSessionActive() || auth.iosReviewOfflineGuardActive || auth.sessionKind === "iosReview",
    hasPunchSelfPermission: auth.access.hasPermission(PERMISSIONS.Attendance.PunchSelf),
    mustChangePassword: auth.user?.mustChangePassword === true,
    userGuid: auth.user?.userGUID,
  };
}

const captureBridge: LocationRequestCaptureBridge = {
  isIosReviewSessionActive,
  getForegroundPermissionsAsync: async () => {
    const permission = await Location.getForegroundPermissionsAsync();
    return { status: permission.status };
  },
  getCurrentPositionAsync: () => Location.getCurrentPositionAsync({ accuracy: Location.Accuracy.High }),
  collectRequiredLocation,
};

const consumer = createLocationRequestConsumer({
  now: () => Date.now(),
  resolveUserGuid: () => resolveLocationRequestUserGuid(readSessionSnapshot()),
  listPending: (userGuid) => locationRequestApi.listPending(userGuid),
  respond: (userGuid, requestGuid, body) => locationRequestApi.respond(userGuid, requestGuid, body),
  captureLocation: (interactive) => captureLocationForRequest({ interactive, bridge: captureBridge }),
  getDeviceContext: getAttendanceDeviceContext,
  showNotice: (notice) => useLocationRequestNoticeStore.getState().show(notice),
  // 只写错误短码与计数，不含坐标。
  warn: (message, detail) => console.warn(message, detail),
});

/**
 * 触发一次位置请求待办消费（永不 reject）。
 * launch / foreground / push-received 是非交互：权限没授予就静默跳过；push-tap / inbox-tap 是交互：允许弹系统定位权限框。
 */
export function consumeLocationRequests(options: { trigger: LocationRequestTrigger }): void {
  void consumer.consume(options);
}

/** 登出 / 账号切换时丢弃节流与去重记录。 */
export function resetLocationRequestConsumer(): void {
  consumer.reset();
}
