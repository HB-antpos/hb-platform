// 打卡异常的展示文案：原因标签（迟到 26 分钟 / 早退 / 无排班 / 重复打卡）与「打卡方式 · 设备 · 定位」。
// 审核卡片与打卡详情共用，保证两处说法一致。
import type { AttendanceApprovalPunch } from "@/modules/attendance/types";
import type { AttendanceStatusTone } from "../AdjustmentFormControls";

type Translate = (key: string, options?: Record<string, unknown>) => string;

const KNOWN_SOURCES = new Set(["PosQr", "ManualAdjustment", "SystemAutoClockOut"]);

/** 门店本地时间里取 HH:mm。 */
export function punchClock(value?: string) {
  const match = value ? /[T ](\d{2}:\d{2})/.exec(value) : null;
  return match?.[1];
}

/** 原因标签：迟到 / 早退带分钟数（红），无排班 / 重复打卡（橙）；未知状态原样显示。 */
export function punchReason(punch: AttendanceApprovalPunch, t: Translate): { label: string; tone: AttendanceStatusTone } {
  const base = "approvals.board.punchInfo.reason";
  switch (punch.status) {
    case "Late":
      return {
        label: punch.lateMinutes !== undefined ? t(`${base}.Late`, { minutes: punch.lateMinutes }) : t(`${base}.LateNoMinutes`),
        tone: "danger",
      };
    case "EarlyLeave":
      return {
        label: punch.earlyLeaveMinutes !== undefined ? t(`${base}.EarlyLeave`, { minutes: punch.earlyLeaveMinutes }) : t(`${base}.EarlyLeaveNoMinutes`),
        tone: "danger",
      };
    case "NoSchedule":
      return { label: t(`${base}.NoSchedule`), tone: "warning" };
    case "Duplicate":
      return { label: t(`${base}.Duplicate`), tone: "warning" };
    default:
      return { label: punch.status, tone: "neutral" };
  }
}

export function punchMethod(punch: AttendanceApprovalPunch, t: Translate) {
  const source = punch.source && KNOWN_SOURCES.has(punch.source) ? punch.source : "other";
  return t(`approvals.board.punchInfo.source.${source}`);
}

export function punchLocation(punch: AttendanceApprovalPunch, t: Translate) {
  if (!punch.hasLocation) return t("approvals.board.punchInfo.locationNone");
  return punch.locationAccuracy !== undefined
    ? t("approvals.board.punchInfo.location", { meters: Math.round(punch.locationAccuracy) })
    : t("approvals.board.punchInfo.locationNoAccuracy");
}

/** 卡片第二行：「扫收银台考勤码 · 设备 POS-02 · 定位 ±12m」。 */
export function punchMetaLine(punch: AttendanceApprovalPunch, t: Translate) {
  return [
    punchMethod(punch, t),
    punch.posDeviceCode ? t("approvals.board.punchInfo.device", { code: punch.posDeviceCode }) : "",
    punchLocation(punch, t),
  ].filter(Boolean).join(" · ");
}
