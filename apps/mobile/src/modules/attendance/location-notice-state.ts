/**
 * 打卡页「定位使用说明」的展示状态判定（纯函数，便于在 Node 下单测）。
 *
 * 展示原则：说明必须与真实采集状态一致——
 * - 只有班中定位任务确实在运行时才显示「已开启」；
 * - 只有确认任务已停止、且今天确实下过班时才显示「已停止」；
 * - 读取状态期间（unknown）不显示任何状态条，避免先闪「已停止」再变「已开启」。
 */

/** 班中定位任务的真实状态：active=在采集，inactive=没有采集，unknown=还在读取。 */
export type ShiftLocationTrackingState = "active" | "inactive" | "unknown";

/** 今日卡主卡片的三种形态，与 TodayPunchCard 的 heroVariant 一致。 */
export type LocationNoticeHeroVariant = "before" | "working" | "summary";

/**
 * notice=上班前说明卡；active=班中定位已开启；stopped=班中定位已停止；none=不显示。
 */
export type LocationNoticeVariant = "notice" | "active" | "stopped" | "none";

export function resolveLocationNoticeVariant(input: {
  heroVariant: LocationNoticeHeroVariant;
  /** 只有当天才允许打卡；历史日期不显示任何定位说明。 */
  allowPunch: boolean;
  tracking: ShiftLocationTrackingState;
  /** 今天是否已经有真实的下班打卡（排除系统补的午夜自动下班卡）。 */
  hasClockedOut: boolean;
}): LocationNoticeVariant {
  if (!input.allowPunch) return "none";
  // 真实在采集永远优先：员工最需要知道的是「此刻正在使用位置」。
  if (input.tracking === "active") return "active";
  if (input.heroVariant === "before") return "notice";
  if (
    input.heroVariant === "summary"
    && input.hasClockedOut
    && input.tracking === "inactive"
  ) {
    return "stopped";
  }
  return "none";
}
