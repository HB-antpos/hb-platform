import { AppAsyncStorage } from "@/shared/storage/async-storage";
import {
  locationSampleGroupKey,
  type QueuedLocationSample,
} from "@/modules/attendance/location-sample-batch";

// 班中定位点的本地持久队列（AsyncStorage，不依赖任何新增原生模块，可 OTA）。
//
// 并发模型：后台任务回调（入队）与前台 flush（取批/删除）会同时读改写同一个存储键，
// 所以本模块所有读改写都经过同一条 promise 链串行执行，禁止出现“读-等-写”交错丢点。

export const LOCATION_SAMPLE_QUEUE_STORAGE_KEY = "hbweb_attendance_location_sample_queue";
/** 队列容量上限：超出时丢弃最旧的点（离线很多天也不会无限涨）。 */
export const LOCATION_SAMPLE_QUEUE_MAX = 3000;

const QUEUE_VERSION = 1;

/** 持久化端口：测试注入内存实现，生产使用 AppAsyncStorage。 */
export interface LocationSampleQueueStorage {
  /** 读取原始持久化内容；不存在返回 null。读取/解析失败应抛错，由队列按空队列处理。 */
  load(): Promise<unknown>;
  save(value: unknown): Promise<void>;
  remove(): Promise<void>;
}

export const asyncStorageLocationSampleQueueStorage: LocationSampleQueueStorage = {
  async load() {
    const raw = await AppAsyncStorage.getString(LOCATION_SAMPLE_QUEUE_STORAGE_KEY);
    return raw ? JSON.parse(raw) : null;
  },
  async save(value) {
    await AppAsyncStorage.setObject(LOCATION_SAMPLE_QUEUE_STORAGE_KEY, value);
  },
  async remove() {
    await AppAsyncStorage.removeItem(LOCATION_SAMPLE_QUEUE_STORAGE_KEY);
  },
};

/** 创建串行执行器：传入的任务严格按调用顺序一个接一个执行，前一个失败不影响后一个。 */
export function createSerialRunner() {
  let tail: Promise<unknown> = Promise.resolve();
  return function runSerially<T>(task: () => Promise<T>): Promise<T> {
    const result = tail.then(task, task);
    tail = result.catch(() => undefined);
    return result;
  };
}

/** 用户标识统一规整：去空白并转小写，GUID 大小写不同也视为同一人。 */
export function normalizeLocationOwnerId(value: unknown): string | undefined {
  if (typeof value !== "string") {
    return undefined;
  }
  const normalized = value.trim().toLowerCase();
  return normalized ? normalized : undefined;
}

function isQueuedSample(value: unknown): value is QueuedLocationSample {
  if (typeof value !== "object" || value === null) {
    return false;
  }
  const sample = value as Record<string, unknown>;
  return (
    typeof sample.clientSampleId === "string"
    && sample.clientSampleId.length > 0
    && typeof sample.ownerUserId === "string"
    && sample.ownerUserId.length > 0
    && typeof sample.storeCode === "string"
    && sample.storeCode.length > 0
    && (sample.eventType === "ShiftSample" || sample.eventType === "ShiftHeartbeat")
    && typeof sample.locationLatitude === "number"
    && Number.isFinite(sample.locationLatitude)
    && typeof sample.locationLongitude === "number"
    && Number.isFinite(sample.locationLongitude)
    && typeof sample.locationCapturedAtUtc === "string"
    && Number.isFinite(Date.parse(sample.locationCapturedAtUtc))
  );
}

/** 从持久化内容里取出合法的点；版本不符或损坏的条目一律忽略。 */
function parseStoredSamples(value: unknown): QueuedLocationSample[] {
  if (typeof value !== "object" || value === null) {
    return [];
  }
  const record = value as { version?: unknown; samples?: unknown };
  if (record.version !== QUEUE_VERSION || !Array.isArray(record.samples)) {
    return [];
  }
  return record.samples.filter(isQueuedSample);
}

export interface EnqueueResult {
  /** 入队后的队列长度。 */
  size: number;
  /** 因超出容量上限而丢弃的最旧点数。 */
  dropped: number;
}

export interface TakeBatchResult {
  /** 本批要上传的点（属于当前用户、同一门店/设备分组、按采集时间升序）。 */
  batch: QueuedLocationSample[];
  /** 本次顺带丢弃的“不属于当前用户”的点数。 */
  discardedForeign: number;
}

export interface LocationSampleQueue {
  enqueue(samples: readonly QueuedLocationSample[]): Promise<EnqueueResult>;
  /**
   * 取下一批要上传的点（不删除）：先丢弃不属于 ownerUserId 的点，
   * 再取最旧点所在分组（门店+设备信息相同）的至多 limit 个点。
   */
  takeBatchForOwner(ownerUserId: string, limit: number): Promise<TakeBatchResult>;
  /** 按 clientSampleId 删除，返回实际删除数量。 */
  removeByIds(ids: Iterable<string>): Promise<number>;
  /** 清空整个队列（登出/会话失效/设备重置）。 */
  clear(): Promise<void>;
  size(): Promise<number>;
}

export function createLocationSampleQueue(options?: {
  storage?: LocationSampleQueueStorage;
  maxSamples?: number;
}): LocationSampleQueue {
  const storage = options?.storage ?? asyncStorageLocationSampleQueueStorage;
  const maxSamples = options?.maxSamples ?? LOCATION_SAMPLE_QUEUE_MAX;
  const runSerially = createSerialRunner();

  async function loadSamples(): Promise<QueuedLocationSample[]> {
    try {
      return parseStoredSamples(await storage.load());
    } catch (error) {
      // 持久化内容损坏（JSON 解析失败等）时按空队列继续，下一次写入会覆盖坏数据，不阻塞采集。
      console.warn("[attendance-location] 读取本地定位队列失败，按空队列处理", error);
      return [];
    }
  }

  async function saveSamples(samples: QueuedLocationSample[]) {
    if (samples.length === 0) {
      await storage.remove();
      return;
    }
    await storage.save({ version: QUEUE_VERSION, samples });
  }

  return {
    enqueue(incoming) {
      return runSerially(async () => {
        if (incoming.length === 0) {
          return { size: (await loadSamples()).length, dropped: 0 };
        }
        const current = await loadSamples();
        const knownIds = new Set(current.map((sample) => sample.clientSampleId));
        const merged = [...current];
        for (const sample of incoming) {
          // 同一个 clientSampleId 只留一份，避免重复入队被后端当成两个点。
          if (!knownIds.has(sample.clientSampleId)) {
            knownIds.add(sample.clientSampleId);
            merged.push({
              ...sample,
              ownerUserId: normalizeLocationOwnerId(sample.ownerUserId) ?? sample.ownerUserId,
            });
          }
        }
        const dropped = Math.max(0, merged.length - maxSamples);
        const kept = dropped > 0 ? merged.slice(dropped) : merged;
        await saveSamples(kept);
        return { size: kept.length, dropped };
      });
    },

    takeBatchForOwner(ownerUserId, limit) {
      return runSerially(async () => {
        const owner = normalizeLocationOwnerId(ownerUserId);
        if (!owner || limit <= 0) {
          return { batch: [], discardedForeign: 0 };
        }
        const current = await loadSamples();
        const own = current.filter(
          (sample) => normalizeLocationOwnerId(sample.ownerUserId) === owner,
        );
        const discardedForeign = current.length - own.length;
        if (discardedForeign > 0) {
          // 换人登录后遗留的上一个人的轨迹：直接丢弃，绝不上传到新账号。
          await saveSamples(own);
        }
        if (own.length === 0) {
          return { batch: [], discardedForeign };
        }
        const ordered = own
          .map((sample, index) => ({ sample, index }))
          .sort((left, right) => {
            const delta = Date.parse(left.sample.locationCapturedAtUtc)
              - Date.parse(right.sample.locationCapturedAtUtc);
            return delta !== 0 ? delta : left.index - right.index;
          })
          .map((entry) => entry.sample);
        const groupKey = locationSampleGroupKey(ordered[0]);
        const batch = ordered
          .filter((sample) => locationSampleGroupKey(sample) === groupKey)
          .slice(0, limit);
        return { batch, discardedForeign };
      });
    },

    removeByIds(ids) {
      return runSerially(async () => {
        const targets = new Set(Array.from(ids, (id) => id.toLowerCase()));
        if (targets.size === 0) {
          return 0;
        }
        const current = await loadSamples();
        const remaining = current.filter(
          (sample) => !targets.has(sample.clientSampleId.toLowerCase()),
        );
        const removed = current.length - remaining.length;
        if (removed > 0) {
          await saveSamples(remaining);
        }
        return removed;
      });
    },

    clear() {
      return runSerially(async () => {
        await storage.remove();
      });
    },

    size() {
      return runSerially(async () => (await loadSamples()).length);
    },
  };
}

/** 生产使用的单例队列（后台任务回调与前台 flush 共用同一个互斥链）。 */
export const locationSampleQueue: LocationSampleQueue = createLocationSampleQueue();
