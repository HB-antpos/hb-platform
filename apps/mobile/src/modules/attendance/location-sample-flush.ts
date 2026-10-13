import {
  LOCATION_SAMPLE_BATCH_MAX,
  buildLocationSampleBatchRequest,
  collectAcknowledgedSampleIds,
  type AttendanceLocationSampleBatchRequest,
  type AttendanceLocationSampleBatchResult,
} from "@/modules/attendance/location-sample-batch";
import type { LocationSampleQueue } from "@/modules/attendance/location-sample-queue";

// 班中定位队列的批量上传（flush）逻辑。全部依赖通过参数注入：
// 队列、当前用户、上传函数、时钟、iOS 审核会话守卫，因此可以在 Node 下完整单测。

/** 失败后的退避：30 秒起步，每次翻倍，封顶 10 分钟。 */
export const LOCATION_FLUSH_BACKOFF_BASE_MS = 30 * 1000;
export const LOCATION_FLUSH_BACKOFF_MAX_MS = 10 * 60 * 1000;
/** 后台回调入队后触发 flush 的阈值：队列 ≥ 5 点，或距上次尝试 ≥ 5 分钟。 */
export const LOCATION_FLUSH_QUEUE_THRESHOLD = 5;
export const LOCATION_FLUSH_MIN_INTERVAL_MS = 5 * 60 * 1000;
/** 单次 flush 最多连发的批数（15 × 200 = 3000，正好覆盖队列上限）。 */
export const LOCATION_FLUSH_MAX_BATCHES = 15;

export type LocationFlushSkipReason = "review-session" | "no-user" | "backoff" | "empty";

export type LocationFlushOutcome =
  | { status: "skipped"; reason: LocationFlushSkipReason }
  | {
    status: "ok";
    /** 本次发出的批数。 */
    batches: number;
    accepted: number;
    duplicates: number;
    rejected: number;
    /** 因不属于当前用户而丢弃的点数。 */
    discardedForeign: number;
    /** 本次因 maxBatches 或中止信号而没发完时为 true。 */
    incomplete: boolean;
  }
  | {
    status: "failed";
    error: unknown;
    /** 失败前已经确认并删除的点数（前几批可能已成功）。 */
    acknowledged: number;
    /** 下一次允许（非强制）尝试的时刻（毫秒时间戳）。 */
    retryAtMs: number;
  };

export interface LocationFlushOptions {
  /** 忽略退避窗口：App 回前台、下班停止时用户期待立即尝试。 */
  ignoreBackoff?: boolean;
  /** 中止信号：下班停止流程超时后取消在途请求。 */
  signal?: AbortSignal;
}

export interface LocationFlushDeps {
  queue: Pick<LocationSampleQueue, "takeBatchForOwner" | "removeByIds">;
  /** 当前登录用户标识；未登录返回 undefined（此时不上传也不丢弃任何点）。 */
  getCurrentUserId: () => Promise<string | undefined> | string | undefined;
  upload: (
    request: AttendanceLocationSampleBatchRequest,
    options: { signal?: AbortSignal },
  ) => Promise<AttendanceLocationSampleBatchResult>;
  /** iOS 审核会话守卫：为真时 flush 直接空操作。 */
  isReviewSessionActive: () => boolean;
  now: () => number;
  log?: (message: string, detail?: unknown) => void;
}

export interface LocationFlushConfig {
  batchSize?: number;
  maxBatches?: number;
  backoffBaseMs?: number;
  backoffMaxMs?: number;
}

/** 后台回调入队后是否应该触发一次 flush（纯函数）。 */
export function shouldFlushAfterEnqueue(input: {
  queueSize: number;
  lastAttemptAtMs: number | undefined;
  nowMs: number;
}) {
  return (
    input.queueSize >= LOCATION_FLUSH_QUEUE_THRESHOLD
    || input.lastAttemptAtMs === undefined
    || input.nowMs - input.lastAttemptAtMs >= LOCATION_FLUSH_MIN_INTERVAL_MS
  );
}

/** 第 failures 次连续失败后的退避时长（failures ≥ 1）。 */
export function computeLocationFlushBackoffMs(
  failures: number,
  baseMs = LOCATION_FLUSH_BACKOFF_BASE_MS,
  maxMs = LOCATION_FLUSH_BACKOFF_MAX_MS,
) {
  const exponent = Math.max(0, failures - 1);
  return Math.min(maxMs, baseMs * 2 ** Math.min(exponent, 30));
}

export function createLocationSampleFlusher(
  deps: LocationFlushDeps,
  config: LocationFlushConfig = {},
) {
  const batchSize = Math.min(config.batchSize ?? LOCATION_SAMPLE_BATCH_MAX, LOCATION_SAMPLE_BATCH_MAX);
  const maxBatches = config.maxBatches ?? LOCATION_FLUSH_MAX_BATCHES;
  const backoffBaseMs = config.backoffBaseMs ?? LOCATION_FLUSH_BACKOFF_BASE_MS;
  const backoffMaxMs = config.backoffMaxMs ?? LOCATION_FLUSH_BACKOFF_MAX_MS;

  // 退避与“上次尝试”只记在进程内：进程重启后第一次触发会立刻尝试，对补传是安全的。
  let consecutiveFailures = 0;
  let nextAttemptAtMs = 0;
  let lastAttemptAtMs: number | undefined;
  let inFlight: Promise<LocationFlushOutcome> | null = null;

  async function resolveUserId() {
    try {
      return await deps.getCurrentUserId();
    } catch (error) {
      deps.log?.("读取当前用户失败，本次不上传定位队列", error);
      return undefined;
    }
  }

  async function run(options: LocationFlushOptions): Promise<LocationFlushOutcome> {
    if (deps.isReviewSessionActive()) {
      return { status: "skipped", reason: "review-session" };
    }
    if (!options.ignoreBackoff && deps.now() < nextAttemptAtMs) {
      return { status: "skipped", reason: "backoff" };
    }
    const userId = await resolveUserId();
    if (!userId) {
      // 没有登录用户时既不能上传，也不能判断归属，所以什么都不动。
      return { status: "skipped", reason: "no-user" };
    }

    let batches = 0;
    let accepted = 0;
    let duplicates = 0;
    let rejected = 0;
    let discardedForeign = 0;
    let acknowledged = 0;
    let incomplete = false;

    const fail = (error: unknown): LocationFlushOutcome => {
      consecutiveFailures += 1;
      const now = deps.now();
      nextAttemptAtMs = now + computeLocationFlushBackoffMs(consecutiveFailures, backoffBaseMs, backoffMaxMs);
      deps.log?.("批量上传班中定位失败，保留队列等待重试", error);
      return { status: "failed", error, acknowledged, retryAtMs: nextAttemptAtMs };
    };

    for (let index = 0; index < maxBatches; index += 1) {
      if (options.signal?.aborted) {
        incomplete = true;
        break;
      }
      const taken = await deps.queue.takeBatchForOwner(userId, batchSize);
      discardedForeign += taken.discardedForeign;
      if (taken.batch.length === 0) {
        break;
      }
      if (batches === 0) {
        // “距上次尝试”以每次 flush 真正发出第一批的时刻为准。
        lastAttemptAtMs = deps.now();
      }

      let result: AttendanceLocationSampleBatchResult;
      try {
        result = await deps.upload(buildLocationSampleBatchRequest(taken.batch), {
          signal: options.signal,
        });
      } catch (error) {
        // 网络错误、5xx、整单失败、响应格式异常：整批保留，按退避重试。
        return fail(error);
      }

      // 只确认“确实在本批里”的 id，防止响应夹带别的 id 误删队列里其他点。
      const batchIds = new Set(taken.batch.map((sample) => sample.clientSampleId.toLowerCase()));
      const ackIds = collectAcknowledgedSampleIds(result).filter((id) => batchIds.has(id.toLowerCase()));
      const removed = ackIds.length === 0 ? 0 : await deps.queue.removeByIds(ackIds);
      batches += 1;
      accepted += result.accepted.length;
      duplicates += result.duplicates.length;
      rejected += result.rejected.length;
      acknowledged += removed;
      if (result.rejected.length > 0) {
        deps.log?.("后端拒绝了部分班中定位点（永久性拒绝，已从本地队列删除）", result.rejected);
      }
      if (removed === 0) {
        // 服务端没有确认本批任何一个点：继续循环只会重复发同一批，按失败处理并退避。
        return fail(new Error("批量定位响应没有确认任何本批样本"));
      }
      if (index === maxBatches - 1) {
        incomplete = true;
      }
    }

    if (batches === 0 && discardedForeign === 0) {
      return { status: "skipped", reason: "empty" };
    }
    // 至少成功了一批且没有失败：重置退避。
    consecutiveFailures = 0;
    nextAttemptAtMs = 0;
    return { status: "ok", batches, accepted, duplicates, rejected, discardedForeign, incomplete };
  }

  return {
    /** 单飞：已有 flush 在进行时直接返回同一个 promise，不会并发发两份。 */
    flush(options: LocationFlushOptions = {}): Promise<LocationFlushOutcome> {
      if (inFlight) {
        return inFlight;
      }
      const current: Promise<LocationFlushOutcome> = run(options)
        .catch((error): LocationFlushOutcome => {
          // run 内部已吞掉上传错误；这里兜住队列读写等意外异常，保证 flush 永不 reject。
          consecutiveFailures += 1;
          nextAttemptAtMs = deps.now()
            + computeLocationFlushBackoffMs(consecutiveFailures, backoffBaseMs, backoffMaxMs);
          deps.log?.("定位队列 flush 出现意外异常", error);
          return { status: "failed", error, acknowledged: 0, retryAtMs: nextAttemptAtMs };
        })
        .finally(() => {
          if (inFlight === current) {
            inFlight = null;
          }
        });
      inFlight = current;
      return current;
    },
    getState() {
      return {
        consecutiveFailures,
        nextAttemptAtMs,
        lastAttemptAtMs,
        inFlight: inFlight !== null,
      };
    },
    getLastAttemptAtMs() {
      return lastAttemptAtMs;
    },
  };
}

export type LocationSampleFlusher = ReturnType<typeof createLocationSampleFlusher>;
