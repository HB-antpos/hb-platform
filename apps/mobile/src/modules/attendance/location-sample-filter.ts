import {
  evaluateAttendanceLocationBatch,
  type AttendanceLocationTrackingWindow,
} from "@/modules/attendance/location-tracking-window";
import type { AttendanceLocationSampleEventType } from "@/modules/attendance/location-sample-batch";

// 班中定位的“逐点过滤”：纯函数，不依赖原生模块。
// 后台任务一次回调可能带来一批坐标，这里按时间升序逐点判断是否保留，
// 保留规则以“上一个保留点”为基准（该基准持久化在班次上下文里，跨回调、跨进程延续）。
// 只按时间采样（约 1 分钟一个点），不再按位移加密：行车途中位移触发会让数据量暴增
// （实测一个班段 1297 点，其中 800 多个点相隔仅数秒），而 1 分钟一点已足够还原轨迹。

/** 与上一个保留点相隔达到该时长即保留。 */
export const LOCATION_KEEP_INTERVAL_MS = 60 * 1000;
/** 与上一个保留点相隔达到该时长，标记为心跳点（说明期间设备一直没有回调）。 */
export const LOCATION_HEARTBEAT_INTERVAL_MS = 5 * 60 * 1000;

/** 一个原始定位（由 expo-location 的 LocationObject 转换而来）。 */
export interface RawLocationFix {
  /** 毫秒时间戳。 */
  timestamp: number;
  latitude: number;
  longitude: number;
  accuracy?: number | null;
  /** 仅 Android 提供；有值才会带上 isMocked。 */
  mocked?: boolean | null;
}

/** 上一个保留点：持久化在班次上下文里。 */
export interface LastKeptLocationPoint {
  capturedAtUtc: string;
  latitude: number;
  longitude: number;
}

export interface KeptLocationSample {
  capturedAtUtc: string;
  latitude: number;
  longitude: number;
  accuracy?: number;
  isMocked?: boolean;
  eventType: AttendanceLocationSampleEventType;
}

export interface LocationFilterResult {
  samples: KeptLocationSample[];
  /** 处理完这一批后的“上一个保留点”（一个都没保留则沿用传入值）。 */
  lastKept?: LastKeptLocationPoint;
}

function isValidCoordinate(latitude: number, longitude: number) {
  return (
    Number.isFinite(latitude)
    && Number.isFinite(longitude)
    && latitude >= -90
    && latitude <= 90
    && longitude >= -180
    && longitude <= 180
  );
}

/** 用现有窗口规则判断单个坐标是否落在班次窗口内（10 小时上限 + 门店本地跨天）。 */
export function isCapturedWithinTrackingWindow(
  window: AttendanceLocationTrackingWindow,
  capturedAtUtc: string,
  nowUtc: string,
) {
  return evaluateAttendanceLocationBatch(window, [capturedAtUtc], nowUtc).capturedAtUtc
    === capturedAtUtc;
}

function parseLastKept(lastKept: LastKeptLocationPoint | undefined) {
  if (!lastKept) {
    return undefined;
  }
  const timestamp = Date.parse(lastKept.capturedAtUtc);
  if (!Number.isFinite(timestamp) || !isValidCoordinate(lastKept.latitude, lastKept.longitude)) {
    // 持久化数据损坏时当作没有基准，下一个点会被保留，不会因此丢轨迹。
    return undefined;
  }
  return { timestamp, latitude: lastKept.latitude, longitude: lastKept.longitude };
}

/**
 * 对一批回调坐标做过滤：
 * 1. 非法时间/坐标、窗口外的点直接排除；
 * 2. 按时间升序依次与“上一个保留点”比较：
 *    - 没有上一个保留点：保留（ShiftSample）；
 *    - 比上一个保留点更早或同一时刻：丢弃（乱序/重复回调）；
 *    - 相隔 ≥ 5 分钟：保留且标记 ShiftHeartbeat（不论位移大小）；
 *    - 相隔 ≥ 60 秒：保留（ShiftSample）；
 *    - 其余丢弃。
 */
export function filterShiftLocationFixes(input: {
  window: AttendanceLocationTrackingWindow;
  fixes: readonly RawLocationFix[];
  lastKept?: LastKeptLocationPoint;
  nowUtc: string;
}): LocationFilterResult {
  const ordered = input.fixes
    .map((fix, index) => ({ fix, index }))
    .filter(({ fix }) => (
      Number.isFinite(fix.timestamp)
      && Number.isFinite(new Date(fix.timestamp).getTime())
      && isValidCoordinate(fix.latitude, fix.longitude)
    ))
    .sort((left, right) => left.fix.timestamp - right.fix.timestamp || left.index - right.index);

  let previous = parseLastKept(input.lastKept);
  let lastKept = input.lastKept;
  const samples: KeptLocationSample[] = [];

  for (const { fix } of ordered) {
    const capturedAtUtc = new Date(fix.timestamp).toISOString();
    if (!isCapturedWithinTrackingWindow(input.window, capturedAtUtc, input.nowUtc)) {
      continue;
    }

    let eventType: AttendanceLocationSampleEventType | undefined;
    if (!previous) {
      eventType = "ShiftSample";
    } else {
      const elapsedMs = fix.timestamp - previous.timestamp;
      if (elapsedMs <= 0) {
        continue;
      }
      if (elapsedMs >= LOCATION_HEARTBEAT_INTERVAL_MS) {
        eventType = "ShiftHeartbeat";
      } else if (elapsedMs >= LOCATION_KEEP_INTERVAL_MS) {
        eventType = "ShiftSample";
      }
    }
    if (!eventType) {
      continue;
    }

    samples.push({
      capturedAtUtc,
      latitude: fix.latitude,
      longitude: fix.longitude,
      ...(typeof fix.accuracy === "number" && Number.isFinite(fix.accuracy)
        ? { accuracy: fix.accuracy }
        : {}),
      ...(typeof fix.mocked === "boolean" ? { isMocked: fix.mocked } : {}),
      eventType,
    });
    lastKept = { capturedAtUtc, latitude: fix.latitude, longitude: fix.longitude };
    previous = { timestamp: fix.timestamp, latitude: fix.latitude, longitude: fix.longitude };
  }

  return { samples, lastKept };
}
