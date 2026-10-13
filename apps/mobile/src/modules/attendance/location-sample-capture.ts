import {
  evaluateAttendanceLocationBatch,
  type AttendanceLocationTrackingWindow,
} from "@/modules/attendance/location-tracking-window";
import {
  filterShiftLocationFixes,
  type LastKeptLocationPoint,
  type RawLocationFix,
} from "@/modules/attendance/location-sample-filter";
import type { QueuedLocationSample } from "@/modules/attendance/location-sample-batch";
import type { LocationSampleQueue } from "@/modules/attendance/location-sample-queue";

// 后台任务回调里“过滤 → 绑定用户 → 入本地队列 → 记录上一个保留点”的编排。
// 全部依赖注入，便于在 Node 下验证顺序与失败路径；与原生模块的连接在 location-tracking.ts。

/** 班次上下文里本模块关心的字段（location-tracking-control 的 ActiveShiftLocationContext 兼容）。 */
export interface ShiftCaptureContext extends AttendanceLocationTrackingWindow {
  storeCode: string;
  hardwareId?: string;
  systemDeviceNumber?: string;
  deviceSystem?: string;
  ownerUserId?: string;
  lastKeptPoint?: LastKeptLocationPoint;
}

export interface ShiftCaptureDeps<C extends ShiftCaptureContext> {
  queue: Pick<LocationSampleQueue, "enqueue" | "removeByIds">;
  readContext: () => Promise<C | null>;
  writeContext: (context: C) => Promise<void>;
  /** 班次上下文没有记录所属用户时（旧版本启动的班次）回退取当前登录用户。 */
  resolveOwnerUserId: () => Promise<string | undefined>;
  createSampleId: () => string;
  log?: (message: string, detail?: unknown) => void;
}

export interface ShiftCaptureResult {
  /** 窗口已到上限（10 小时/门店本地跨天）：调用方应停止原生任务。 */
  shouldStop: boolean;
  /** 本批入队的点数。 */
  enqueued: number;
  /** 入队后的队列长度；本批没有入队时为 undefined。 */
  queueSize?: number;
  /** 因超出队列容量而丢弃的最旧点数。 */
  dropped: number;
}

/**
 * 处理一批后台定位回调：
 * 1. 用窗口规则 + 位移/时间规则逐点过滤；
 * 2. 绑定所属用户（上下文里记录的 ownerUserId，缺失时回退当前登录用户）后入队；
 * 3. 入队成功后再把“上一个保留点”写回班次上下文（先入队再记基准：中途失败最多多保留一个点，不会丢点）。
 */
export async function captureShiftLocationFixes<C extends ShiftCaptureContext>(
  deps: ShiftCaptureDeps<C>,
  context: C,
  fixes: readonly RawLocationFix[],
  nowUtc: string,
): Promise<ShiftCaptureResult> {
  const capturedAtValues = fixes
    .filter((fix) => Number.isFinite(fix.timestamp) && Number.isFinite(new Date(fix.timestamp).getTime()))
    .map((fix) => new Date(fix.timestamp).toISOString());
  const decision = evaluateAttendanceLocationBatch(context, capturedAtValues, nowUtc);

  const filtered = filterShiftLocationFixes({
    window: context,
    fixes,
    lastKept: context.lastKeptPoint,
    nowUtc,
  });
  if (filtered.samples.length === 0) {
    return { shouldStop: decision.shouldStop, enqueued: 0, dropped: 0 };
  }

  const ownerUserId = context.ownerUserId?.trim() || (await deps.resolveOwnerUserId());
  if (!ownerUserId) {
    // 无法确定这些点属于谁：宁可丢弃，也不能让它们在换人登录后被上传到别人名下。
    deps.log?.("无法确定班中定位点的所属用户，本批定位点已丢弃");
    return { shouldStop: decision.shouldStop, enqueued: 0, dropped: 0 };
  }

  const queued: QueuedLocationSample[] = filtered.samples.map((sample) => ({
    clientSampleId: deps.createSampleId(),
    ownerUserId,
    storeCode: context.storeCode,
    hardwareId: context.hardwareId,
    systemDeviceNumber: context.systemDeviceNumber,
    deviceSystem: context.deviceSystem,
    eventType: sample.eventType,
    locationLatitude: sample.latitude,
    locationLongitude: sample.longitude,
    ...(sample.accuracy !== undefined ? { locationAccuracy: sample.accuracy } : {}),
    ...(sample.isMocked !== undefined ? { isMocked: sample.isMocked } : {}),
    locationCapturedAtUtc: sample.capturedAtUtc,
  }));

  const enqueueResult = await deps.queue.enqueue(queued);

  if (!decision.shouldStop) {
    // 重新读取上下文再写回：期间若已下班/登出（上下文被清除），不能把它写回来，
    // 同时撤回刚入队的点，避免遗留一条已停止班次的轨迹。
    const latest = await deps.readContext();
    if (!latest) {
      await deps.queue.removeByIds(queued.map((sample) => sample.clientSampleId));
      return { shouldStop: false, enqueued: 0, dropped: enqueueResult.dropped };
    }
    await deps.writeContext({
      ...latest,
      ownerUserId,
      lastKeptPoint: filtered.lastKept,
    });
  }

  return {
    shouldStop: decision.shouldStop,
    enqueued: queued.length,
    queueSize: enqueueResult.size,
    dropped: enqueueResult.dropped,
  };
}
