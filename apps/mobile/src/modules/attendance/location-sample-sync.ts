import { postAttendanceLocationSamplesBatch } from "@/modules/attendance/location-sample-batch-api";
import {
  createLocationSampleFlusher,
  type LocationFlushOutcome,
} from "@/modules/attendance/location-sample-flush";
import { resolveCurrentLocationOwnerId } from "@/modules/attendance/location-sample-owner";
import { locationSampleQueue } from "@/modules/attendance/location-sample-queue";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";

// 班中定位队列上传的运行时装配：把纯逻辑的 flusher 接上真实队列、当前用户和 apiClient。

/** 下班停止流程里等待上传的上限；超时放弃，剩余点保留到下次回前台再传。 */
export const LOCATION_STOP_FLUSH_TIMEOUT_MS = 8 * 1000;

export const locationSampleFlusher = createLocationSampleFlusher({
  queue: locationSampleQueue,
  getCurrentUserId: resolveCurrentLocationOwnerId,
  upload: postAttendanceLocationSamplesBatch,
  isReviewSessionActive: isIosReviewSessionActive,
  now: Date.now,
  log: (message, detail) => {
    console.warn(`[attendance-location] ${message}`, detail);
  },
});

/** 立即尝试把本地队列批量上传（单飞；退避窗口内默认不发，ignoreBackoff 可强制）。 */
export function flushPendingLocationSamples(options?: { ignoreBackoff?: boolean }) {
  return locationSampleFlusher.flush(options);
}

/**
 * 在有上限的时间内尽力上传队列：超时即中止在途请求并返回，绝不抛错，
 * 因此调用方（下班停止流程）不会被上传阻塞或打断。
 */
export async function flushPendingLocationSamplesWithin(
  timeoutMs: number = LOCATION_STOP_FLUSH_TIMEOUT_MS,
): Promise<LocationFlushOutcome | "timeout"> {
  const controller = new AbortController();
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<"timeout">((resolve) => {
    timer = setTimeout(() => {
      // 中止在途请求；若 flush 是加入了别处已在进行的单飞（没吃到这个 signal），
      // 这里的 resolve 也保证调用方最多只等 timeoutMs。
      controller.abort();
      resolve("timeout");
    }, timeoutMs);
  });
  try {
    return await Promise.race([
      locationSampleFlusher.flush({ ignoreBackoff: true, signal: controller.signal }),
      timeout,
    ]);
  } finally {
    if (timer) {
      clearTimeout(timer);
    }
  }
}
