export const ATTENDANCE_LOCATION_MAX_TRACKING_MS = 10 * 60 * 60 * 1000;

export interface AttendanceLocationTrackingWindow {
  startedAtUtc: string;
  workDate: string;
  storeTimeZone: string;
}

export interface AttendanceLocationBatchDecision {
  shouldStop: boolean;
  capturedAtUtc?: string;
}

const utcTextPattern = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,7})?(Z|[+-]\d{2}:\d{2})?$/i;

function utcTimestamp(value?: string) {
  const trimmed = value?.trim();
  if (!trimmed || !utcTextPattern.test(trimmed)) {
    return undefined;
  }
  // 服务端 SQL DateTime UTC 字段可能不带 Z；这里只处理明确的 UTC 语义字段。
  const normalized = /(?:Z|[+-]\d{2}:\d{2})$/i.test(trimmed)
    ? trimmed
    : `${trimmed}Z`;
  const timestamp = Date.parse(normalized);
  return Number.isFinite(timestamp) ? timestamp : undefined;
}

function dateInTimeZone(timestamp: number, timeZone: string) {
  try {
    const parts = new Intl.DateTimeFormat("en-CA", {
      timeZone,
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
    }).formatToParts(new Date(timestamp));
    const year = parts.find((part) => part.type === "year")?.value;
    const month = parts.find((part) => part.type === "month")?.value;
    const day = parts.find((part) => part.type === "day")?.value;
    return year && month && day ? `${year}-${month}-${day}` : undefined;
  } catch {
    return undefined;
  }
}

export function evaluateAttendanceLocationBatch(
  window: AttendanceLocationTrackingWindow,
  capturedAtUtcValues: string[],
  nowUtc: string,
): AttendanceLocationBatchDecision {
  const startedAt = utcTimestamp(window.startedAtUtc);
  const now = utcTimestamp(nowUtc);
  const workDate = /^\d{4}-\d{2}-\d{2}$/.test(window.workDate)
    ? window.workDate
    : undefined;
  const startedDate = startedAt === undefined
    ? undefined
    : dateInTimeZone(startedAt, window.storeTimeZone);
  const nowDate = now === undefined
    ? undefined
    : dateInTimeZone(now, window.storeTimeZone);
  if (
    startedAt === undefined
    || now === undefined
    || !workDate
    || startedDate !== workDate
    || !nowDate
    || now < startedAt
  ) {
    return { shouldStop: true, capturedAtUtc: undefined };
  }

  const maximumEnd = startedAt + ATTENDANCE_LOCATION_MAX_TRACKING_MS;
  const shouldStop = now >= maximumEnd || nowDate !== workDate;
  const capturedAtUtc = capturedAtUtcValues
    .map((value, index) => ({
      value,
      index,
      timestamp: utcTimestamp(value),
    }))
    .filter((item): item is { value: string; index: number; timestamp: number } => (
      item.timestamp !== undefined
      && item.timestamp >= startedAt
      && item.timestamp < maximumEnd
      && item.timestamp <= now
      && dateInTimeZone(item.timestamp, window.storeTimeZone) === workDate
    ))
    .sort((left, right) => right.timestamp - left.timestamp || right.index - left.index)
    .at(0)?.value;

  return {
    shouldStop,
    capturedAtUtc,
  };
}

/**
 * 自愈/回前台检查用的“窗口是否已到期”判断。
 *
 * 与 evaluateAttendanceLocationBatch 的区别：startedAtUtc 来自服务端打卡时间，设备时钟略慢时
 * now 会早于 startedAt，后者会因此报告 shouldStop；回前台检查不能把刚上班的任务误判为到期而停掉，
 * 所以 now 早于 startedAt 时按 startedAt 计（任务回调自己的判断逻辑保持不变）。
 */
export function isAttendanceLocationWindowExpired(
  window: AttendanceLocationTrackingWindow,
  nowUtc: string,
) {
  const startedAt = utcTimestamp(window.startedAtUtc);
  const now = utcTimestamp(nowUtc);
  const effectiveNowUtc = startedAt !== undefined && now !== undefined && now < startedAt
    ? window.startedAtUtc
    : nowUtc;
  return evaluateAttendanceLocationBatch(window, [], effectiveNowUtc).shouldStop;
}
