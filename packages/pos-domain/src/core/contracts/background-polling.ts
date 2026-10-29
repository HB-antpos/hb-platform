/**
 * 后台轮询节奏（对齐 WPF BackgroundPollingSchedule，#926/#927）。
 *
 * 收银设备整夜开着时，健康探测、小票资料检查、考勤二维码登记仍按白天节奏发请求，夜里没有顾客操作，
 * 这些请求只是白占网络与服务器。设计要点：
 * - 只调整「周期性后台轮询」的间隔，不停止它们：保留离线恢复、待上传订单重试，只是变慢；
 * - 夜间静默窗口按设备本地时间判断（门店设备的系统时间就是门店所在地时间）：22:00（含）–06:00（不含）；
 * - 异常（离线、探测失败、启用失败等）时一律按原间隔重试，保证恢复后提示尽快消失；
 * - 付款等关键路径会主动检查在线状态，不依赖这些周期。
 */

/** 夜间静默窗口起点（设备本地小时，含）。 */
export const QUIET_HOURS_START = 22;
/** 夜间静默窗口终点（设备本地小时，不含）。 */
export const QUIET_HOURS_END = 6;
/** 夜间静默窗口内的后台轮询间隔。 */
export const QUIET_POLLING_INTERVAL_MS = 5 * 60 * 1_000;
/** 考勤二维码面板状态健康（在线、已启用、校时有效）时的后台刷新间隔，白天夜里相同（WPF #927）。 */
export const ATTENDANCE_QR_HEALTHY_REFRESH_INTERVAL_MS = 5 * 60 * 1_000;
/** 考勤二维码面板异常（离线、启用失败、需要重新校时等）时的快速重试间隔。 */
export const ATTENDANCE_QR_RETRY_REFRESH_INTERVAL_MS = 15_000;

/** 给定本地时间是否处于夜间静默窗口（跨午夜：22:00 ≤ t 或 t < 06:00）。 */
export function isQuietHours(localNow: Date): boolean {
  const hour = localNow.getHours();
  return hour >= QUIET_HOURS_START || hour < QUIET_HOURS_END;
}

/**
 * 当前应使用的轮询间隔：健康且处于夜间静默窗口时取 5 分钟（但绝不比原间隔更短），其余按原间隔。
 * healthy=false（离线、探测失败等）时即使在夜里也按原间隔重试。
 */
export function getBackgroundPollingIntervalMs(
  localNow: Date,
  normalIntervalMs: number,
  healthy = true,
): number {
  return healthy && isQuietHours(localNow) && normalIntervalMs < QUIET_POLLING_INTERVAL_MS
    ? QUIET_POLLING_INTERVAL_MS
    : normalIntervalMs;
}

/** 考勤二维码面板下一次后台刷新应等待多久：状态健康 5 分钟，否则 15 秒重试。 */
export function getAttendanceQrRefreshIntervalMs(healthy: boolean): number {
  return healthy
    ? ATTENDANCE_QR_HEALTHY_REFRESH_INTERVAL_MS
    : ATTENDANCE_QR_RETRY_REFRESH_INTERVAL_MS;
}

/** 定时器抖动容差：基础定时器按固定节拍唤醒，略早于目标间隔的唤醒也视为到期。 */
const DUE_TOLERANCE_MS = 1_000;

/**
 * 基础定时器唤醒时是否该真正执行一次后台任务。
 * lastRunAtMs 为 null（从未执行）或系统时钟回拨时视为到期；否则要求距上次执行已满 intervalMs。
 */
export function isBackgroundPollDue(
  nowMs: number,
  lastRunAtMs: number | null,
  intervalMs: number,
): boolean {
  if (lastRunAtMs === null || nowMs < lastRunAtMs) return true;
  return nowMs - lastRunAtMs >= intervalMs - DUE_TOLERANCE_MS;
}
