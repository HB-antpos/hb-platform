import assert from "node:assert/strict";
import { test } from "node:test";
import {
  captureShiftLocationFixes,
  type ShiftCaptureContext,
  type ShiftCaptureDeps,
} from "./location-sample-capture";
import type { RawLocationFix } from "./location-sample-filter";
import {
  createLocationSampleQueue,
  type LocationSampleQueueStorage,
} from "./location-sample-queue";

function createMemoryStorage(): LocationSampleQueueStorage {
  let value: unknown = null;
  return {
    async load() {
      return value === null ? null : structuredClone(value);
    },
    async save(next) {
      value = structuredClone(next);
    },
    async remove() {
      value = null;
    },
  };
}

const T0 = Date.parse("2026-10-13T02:00:00Z");
const NOW = "2026-10-13T05:00:00Z";
const LAT = -33.8688;
const LNG = 151.2093;

function fix(offsetSeconds: number, extra: Partial<RawLocationFix> = {}): RawLocationFix {
  return { timestamp: T0 + offsetSeconds * 1000, latitude: LAT, longitude: LNG, accuracy: 9, ...extra };
}

const baseContext: ShiftCaptureContext = {
  storeCode: "1042",
  hardwareId: "hw-1",
  systemDeviceNumber: "SYS-1",
  deviceSystem: "android",
  startedAtUtc: "2026-10-13T01:00:00Z",
  workDate: "2026-10-13",
  storeTimeZone: "UTC",
  ownerUserId: "user-a",
};

// owner 传 null 表示“当前没有可识别的登录用户”（不能用 undefined，会触发参数默认值）。
function setup(initial: ShiftCaptureContext | null = baseContext, owner: string | null = "fallback-user") {
  const queue = createLocationSampleQueue({ storage: createMemoryStorage() });
  const state = { context: initial === null ? null : { ...initial } };
  const writes: ShiftCaptureContext[] = [];
  let sequence = 0;
  const logs: string[] = [];
  const deps: ShiftCaptureDeps<ShiftCaptureContext> = {
    queue,
    readContext: async () => (state.context ? { ...state.context } : null),
    writeContext: async (next) => {
      writes.push(next);
      state.context = { ...next };
    },
    resolveOwnerUserId: async () => owner ?? undefined,
    createSampleId: () => `sample-${++sequence}`,
    log: (message) => {
      logs.push(message);
    },
  };
  return { queue, state, writes, deps, logs };
}

test("过滤后的点绑定所属用户入队，并把上一个保留点写回班次上下文", async () => {
  const { queue, deps, writes, state } = setup();
  const result = await captureShiftLocationFixes(deps, baseContext, [fix(0), fix(60, { mocked: true })], NOW);

  assert.deepEqual(result, { shouldStop: false, enqueued: 2, queueSize: 2, dropped: 0 });
  const { batch } = await queue.takeBatchForOwner("user-a", 200);
  assert.deepEqual(
    batch.map((item) => ({
      id: item.clientSampleId,
      owner: item.ownerUserId,
      store: item.storeCode,
      device: item.hardwareId,
      eventType: item.eventType,
      isMocked: item.isMocked,
      accuracy: item.locationAccuracy,
    })),
    [
      { id: "sample-1", owner: "user-a", store: "1042", device: "hw-1", eventType: "ShiftSample", isMocked: undefined, accuracy: 9 },
      { id: "sample-2", owner: "user-a", store: "1042", device: "hw-1", eventType: "ShiftSample", isMocked: true, accuracy: 9 },
    ],
  );
  assert.equal(writes.length, 1);
  assert.deepEqual(state.context?.lastKeptPoint, {
    capturedAtUtc: "2026-10-13T02:01:00.000Z",
    latitude: LAT,
    longitude: LNG,
  });
  assert.equal(state.context?.ownerUserId, "user-a");
});

test("上一个保留点跨回调延续：第二次回调里太近的点不再入队，5 分钟后的点是心跳", async () => {
  const { queue, deps, state } = setup();
  await captureShiftLocationFixes(deps, baseContext, [fix(0)], NOW);

  const second = await captureShiftLocationFixes(deps, state.context as ShiftCaptureContext, [fix(20)], NOW);
  assert.equal(second.enqueued, 0);
  assert.equal(await queue.size(), 1);

  const third = await captureShiftLocationFixes(deps, state.context as ShiftCaptureContext, [fix(300)], NOW);
  assert.equal(third.enqueued, 1);
  const { batch } = await queue.takeBatchForOwner("user-a", 200);
  assert.equal(batch[1].eventType, "ShiftHeartbeat");
});

test("班次上下文没有所属用户（旧版本启动的班次）时回退当前登录用户，并持久化到上下文", async () => {
  const legacy: ShiftCaptureContext = { ...baseContext, ownerUserId: undefined };
  const { queue, deps, state } = setup(legacy, "fallback-user");
  await captureShiftLocationFixes(deps, legacy, [fix(0)], NOW);
  const { batch } = await queue.takeBatchForOwner("fallback-user", 200);
  assert.equal(batch.length, 1);
  assert.equal(state.context?.ownerUserId, "fallback-user");
});

test("无法确定所属用户时丢弃本批点，不入队也不改上下文", async () => {
  const legacy: ShiftCaptureContext = { ...baseContext, ownerUserId: undefined };
  const { queue, deps, writes, logs } = setup(legacy, null);
  const result = await captureShiftLocationFixes(deps, legacy, [fix(0)], NOW);
  assert.deepEqual(result, { shouldStop: false, enqueued: 0, dropped: 0 });
  assert.equal(await queue.size(), 0);
  assert.equal(writes.length, 0);
  assert.equal(logs.length, 1);
});

test("入队期间班次上下文已被清除（下班/登出）：不写回上下文，并撤回刚入队的点", async () => {
  const { queue, deps, state, writes } = setup();
  const originalEnqueue = queue.enqueue.bind(queue);
  deps.queue = {
    ...queue,
    enqueue: async (samples) => {
      const result = await originalEnqueue(samples);
      state.context = null; // 入队完成的瞬间用户下班了
      return result;
    },
    removeByIds: queue.removeByIds.bind(queue),
  };
  const result = await captureShiftLocationFixes(deps, baseContext, [fix(0)], NOW);
  assert.equal(result.enqueued, 0);
  assert.equal(writes.length, 0, "已清除的上下文不能被写回");
  assert.equal(await queue.size(), 0);
});

test("窗口到期（满 10 小时）：窗口内的点照常入队，shouldStop=true，且不再写回上下文", async () => {
  const { queue, deps, writes } = setup();
  const result = await captureShiftLocationFixes(
    deps,
    baseContext,
    [
      { timestamp: Date.parse("2026-10-13T10:59:00Z"), latitude: LAT, longitude: LNG },
      { timestamp: Date.parse("2026-10-13T11:00:00Z"), latitude: LAT, longitude: LNG }, // 截止点及其后不得入队
    ],
    "2026-10-13T11:00:30Z",
  );
  assert.equal(result.shouldStop, true);
  assert.equal(result.enqueued, 1);
  assert.equal(await queue.size(), 1);
  assert.equal(writes.length, 0);
});

test("窗口已失效（工作日不符）：一个点都不入队，shouldStop=true", async () => {
  const context: ShiftCaptureContext = { ...baseContext, workDate: "2026-10-12" };
  const { queue, deps } = setup(context);
  const result = await captureShiftLocationFixes(deps, context, [fix(0)], NOW);
  assert.deepEqual(result, { shouldStop: true, enqueued: 0, dropped: 0 });
  assert.equal(await queue.size(), 0);
});

test("回调里没有任何坐标：不入队、不写上下文", async () => {
  const { queue, deps, writes } = setup();
  const result = await captureShiftLocationFixes(deps, baseContext, [], NOW);
  assert.deepEqual(result, { shouldStop: false, enqueued: 0, dropped: 0 });
  assert.equal(await queue.size(), 0);
  assert.equal(writes.length, 0);
});

test("队列超容量时回报丢弃数量", async () => {
  const queue = createLocationSampleQueue({ storage: createMemoryStorage(), maxSamples: 2 });
  const { deps } = setup();
  deps.queue = queue;
  const result = await captureShiftLocationFixes(deps, baseContext, [fix(0), fix(60), fix(120)], NOW);
  assert.deepEqual({ enqueued: result.enqueued, queueSize: result.queueSize, dropped: result.dropped }, {
    enqueued: 3,
    queueSize: 2,
    dropped: 1,
  });
});
