import assert from "node:assert/strict";
import { test } from "node:test";
import {
  LocationSampleBatchResponseError,
  locationSampleGroupKey,
  type AttendanceLocationSampleBatchRequest,
  type AttendanceLocationSampleBatchResult,
  type QueuedLocationSample,
} from "./location-sample-batch";
import {
  LOCATION_FLUSH_BACKOFF_BASE_MS,
  LOCATION_FLUSH_BACKOFF_MAX_MS,
  LOCATION_FLUSH_BUSINESS_FAILURE_DROP_THRESHOLD,
  computeLocationFlushBackoffMs,
  createLocationSampleFlusher,
  isDefiniteBusinessFailure,
  shouldFlushAfterEnqueue,
  type LocationFlushDeps,
} from "./location-sample-flush";
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

let counter = 0;
function sample(overrides: Partial<QueuedLocationSample> = {}): QueuedLocationSample {
  counter += 1;
  return {
    clientSampleId: `id-${String(counter).padStart(5, "0")}`,
    ownerUserId: "user-a",
    storeCode: "1042",
    eventType: "ShiftSample",
    locationLatitude: -33.8688,
    locationLongitude: 151.2093,
    locationCapturedAtUtc: new Date(Date.UTC(2026, 9, 13, 0, 0, 0) + counter * 1000).toISOString(),
    ...overrides,
  };
}

type UploadFn = LocationFlushDeps["upload"];

function setup(options: {
  upload: UploadFn;
  userId?: string | undefined;
  reviewActive?: boolean;
  config?: Parameters<typeof createLocationSampleFlusher>[1];
}) {
  const queue = createLocationSampleQueue({ storage: createMemoryStorage() });
  const clock = { now: 1_000_000 };
  const uploads: AttendanceLocationSampleBatchRequest[] = [];
  const logs: string[] = [];
  const flusher = createLocationSampleFlusher(
    {
      queue,
      getCurrentUserId: () => ("userId" in options ? options.userId : "user-a"),
      upload: async (request, uploadOptions) => {
        uploads.push(request);
        return options.upload(request, uploadOptions);
      },
      isReviewSessionActive: () => options.reviewActive ?? false,
      now: () => clock.now,
      log: (message) => {
        logs.push(message);
      },
    },
    options.config,
  );
  return { queue, clock, uploads, logs, flusher };
}

const acceptAll: UploadFn = async (request) => ({
  accepted: request.samples.map((item) => item.clientSampleId),
  duplicates: [],
  rejected: [],
});

test("退避：30s、60s、…… 翻倍并封顶 10 分钟", () => {
  assert.equal(LOCATION_FLUSH_BACKOFF_BASE_MS, 30_000);
  assert.equal(LOCATION_FLUSH_BACKOFF_MAX_MS, 600_000);
  assert.deepEqual(
    [1, 2, 3, 4, 5, 6, 20, 100].map((failures) => computeLocationFlushBackoffMs(failures)),
    [30_000, 60_000, 120_000, 240_000, 480_000, 600_000, 600_000, 600_000],
  );
});

test("入队后的触发条件：队列 ≥ 5 点，或从未尝试 / 距上次尝试 ≥ 5 分钟", () => {
  const now = 10_000_000;
  assert.equal(shouldFlushAfterEnqueue({ queueSize: 5, lastAttemptAtMs: now - 1000, nowMs: now }), true);
  assert.equal(shouldFlushAfterEnqueue({ queueSize: 4, lastAttemptAtMs: now - 1000, nowMs: now }), false);
  assert.equal(shouldFlushAfterEnqueue({ queueSize: 1, lastAttemptAtMs: undefined, nowMs: now }), true);
  assert.equal(shouldFlushAfterEnqueue({ queueSize: 1, lastAttemptAtMs: now - 299_999, nowMs: now }), false);
  assert.equal(shouldFlushAfterEnqueue({ queueSize: 1, lastAttemptAtMs: now - 300_000, nowMs: now }), true);
});

test("iOS 审核会话：flush 直接空操作，不读用户、不上传、不动队列", async () => {
  const { queue, uploads, flusher } = setup({ upload: acceptAll, reviewActive: true });
  await queue.enqueue([sample()]);
  assert.deepEqual(await flusher.flush(), { status: "skipped", reason: "review-session" });
  assert.equal(uploads.length, 0);
  assert.equal(await queue.size(), 1);
});

test("没有登录用户：不上传也不丢弃任何点", async () => {
  const { queue, uploads, flusher } = setup({ upload: acceptAll, userId: undefined });
  await queue.enqueue([sample(), sample({ ownerUserId: "user-b" })]);
  assert.deepEqual(await flusher.flush(), { status: "skipped", reason: "no-user" });
  assert.equal(uploads.length, 0);
  assert.equal(await queue.size(), 2);
});

test("队列为空：skipped/empty", async () => {
  const { flusher, uploads } = setup({ upload: acceptAll });
  assert.deepEqual(await flusher.flush(), { status: "skipped", reason: "empty" });
  assert.equal(uploads.length, 0);
});

test("成功：accepted 的点从队列删除，并重置退避", async () => {
  const { queue, flusher, uploads } = setup({ upload: acceptAll });
  await queue.enqueue([sample(), sample(), sample()]);
  const outcome = await flusher.flush();
  assert.deepEqual(outcome, {
    status: "ok",
    batches: 1,
    accepted: 3,
    duplicates: 0,
    rejected: 0,
    discardedForeign: 0,
    incomplete: false,
  });
  assert.equal(uploads.length, 1);
  assert.equal(await queue.size(), 0);
  assert.equal(flusher.getState().consecutiveFailures, 0);
});

test("accepted / duplicates / rejected 里的点全部从本地队列删除", async () => {
  const items = [sample(), sample(), sample(), sample()];
  const { queue, flusher } = setup({
    upload: async () => ({
      accepted: [items[0].clientSampleId],
      duplicates: [items[1].clientSampleId],
      rejected: [
        { clientSampleId: items[2].clientSampleId, errorCode: "SAMPLE_TOO_OLD", message: "too old" },
        { clientSampleId: items[3].clientSampleId, errorCode: "ACTIVE_SHIFT_REQUIRED", message: "no shift" },
      ],
    }),
  });
  await queue.enqueue(items);
  const outcome = await flusher.flush();
  assert.equal(outcome.status, "ok");
  assert.equal(await queue.size(), 0, "永久性拒绝的点也必须删除，否则会一直重试");
  if (outcome.status === "ok") {
    assert.equal(outcome.rejected, 2);
  }
});

test("响应夹带的、不在本批里的 id 不会误删队列里别的点", async () => {
  const [inBatch, later] = [sample(), sample({ storeCode: "2000" })];
  const { queue, flusher } = setup({
    upload: async () => ({ accepted: [inBatch.clientSampleId, later.clientSampleId], duplicates: [], rejected: [] }),
    config: { maxBatches: 1 },
  });
  await queue.enqueue([inBatch, later]);
  await flusher.flush();
  const remaining = await queue.takeBatchForOwner("user-a", 200);
  assert.deepEqual(remaining.batch.map((item) => item.clientSampleId), [later.clientSampleId]);
});

test("未被响应确认的点保留；整批无任何确认按失败处理并退避（避免死循环重发同一批）", async () => {
  const items = [sample(), sample(), sample()];
  let calls = 0;
  const { queue, flusher, clock } = setup({
    upload: async (request) => {
      calls += 1;
      return calls === 1
        ? { accepted: [request.samples[0].clientSampleId], duplicates: [], rejected: [] }
        : { accepted: [], duplicates: [], rejected: [] };
    },
  });
  await queue.enqueue(items);
  const outcome = await flusher.flush();
  assert.equal(outcome.status, "failed");
  if (outcome.status === "failed") {
    assert.equal(outcome.acknowledged, 1);
    assert.equal(outcome.retryAtMs, clock.now + 30_000);
  }
  assert.equal(calls, 2);
  assert.equal(await queue.size(), 2);
});

test("整单失败（网络错误 / 5xx / 响应格式异常）保留全部点，按指数退避，成功后重置", async () => {
  let mode: "network" | "server" | "malformed" | "ok" = "network";
  const { queue, flusher, clock, uploads } = setup({
    upload: async (request) => {
      if (mode === "network") throw new Error("Network Error");
      if (mode === "server") throw Object.assign(new Error("500"), { response: { status: 500 } });
      if (mode === "malformed") throw new LocationSampleBatchResponseError("bad body");
      return acceptAll(request, {});
    },
  });
  await queue.enqueue([sample(), sample()]);

  const first = await flusher.flush();
  assert.equal(first.status, "failed");
  assert.equal(await queue.size(), 2);
  assert.equal(flusher.getState().nextAttemptAtMs, clock.now + 30_000);

  // 退避窗口内的普通触发被挡住，不会发请求
  const blocked = await flusher.flush();
  assert.deepEqual(blocked, { status: "skipped", reason: "backoff" });
  assert.equal(uploads.length, 1);

  // 窗口到期后再失败：60s
  clock.now += 30_000;
  mode = "server";
  assert.equal((await flusher.flush()).status, "failed");
  assert.equal(flusher.getState().nextAttemptAtMs, clock.now + 60_000);

  // ignoreBackoff（回前台 / 下班）可以无视退避窗口；这次响应格式异常：120s
  mode = "malformed";
  assert.equal((await flusher.flush({ ignoreBackoff: true })).status, "failed");
  assert.equal(flusher.getState().consecutiveFailures, 3);
  assert.equal(flusher.getState().nextAttemptAtMs, clock.now + 120_000);
  assert.equal(await queue.size(), 2);

  // 成功后清空队列并重置退避
  mode = "ok";
  assert.equal((await flusher.flush({ ignoreBackoff: true })).status, "ok");
  assert.equal(await queue.size(), 0);
  assert.equal(flusher.getState().consecutiveFailures, 0);
  assert.equal(flusher.getState().nextAttemptAtMs, 0);

  // 重置后下一轮失败又从 30s 起步
  mode = "network";
  await queue.enqueue([sample()]);
  await flusher.flush();
  assert.equal(flusher.getState().nextAttemptAtMs, clock.now + 30_000);
});

test("退避封顶 10 分钟", async () => {
  const { queue, flusher, clock } = setup({
    upload: async () => {
      throw new Error("down");
    },
  });
  await queue.enqueue([sample()]);
  for (let index = 0; index < 8; index += 1) {
    await flusher.flush({ ignoreBackoff: true });
  }
  assert.equal(flusher.getState().nextAttemptAtMs, clock.now + 600_000);
});

test("单飞：并发触发只发一份请求，所有调用拿到同一个结果", async () => {
  let release: (() => void) | undefined;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  const { queue, flusher, uploads } = setup({
    upload: async (request) => {
      await gate;
      return acceptAll(request, {});
    },
  });
  await queue.enqueue([sample(), sample()]);

  const first = flusher.flush();
  const second = flusher.flush({ ignoreBackoff: true });
  assert.equal(first, second, "单飞期间应返回同一个 promise");
  assert.equal(flusher.getState().inFlight, true);
  release?.();
  const [a, b] = await Promise.all([first, second]);
  assert.deepEqual(a, b);
  assert.equal(uploads.length, 1);
  assert.equal(flusher.getState().inFlight, false);

  // 结束后可以再次发起
  await queue.enqueue([sample()]);
  assert.equal((await flusher.flush()).status, "ok");
  assert.equal(uploads.length, 2);
});

test("每批最多 200 点，按采集时间升序连发，直到队列清空", async () => {
  const { queue, flusher, uploads } = setup({ upload: acceptAll });
  const items = Array.from({ length: 450 }, () => sample());
  // 乱序入队
  await queue.enqueue([...items].reverse());
  const outcome = await flusher.flush();
  assert.deepEqual(uploads.map((request) => request.samples.length), [200, 200, 50]);
  const sent = uploads.flatMap((request) => request.samples.map((item) => item.clientSampleId));
  assert.deepEqual(sent, items.map((item) => item.clientSampleId), "全局按采集时间升序");
  assert.equal(outcome.status, "ok");
  assert.equal(await queue.size(), 0);
});

test("单次 flush 连发批数有上限；没发完时 incomplete=true，剩余保留", async () => {
  const { queue, flusher, uploads } = setup({
    upload: acceptAll,
    config: { batchSize: 10, maxBatches: 2 },
  });
  await queue.enqueue(Array.from({ length: 35 }, () => sample()));
  const outcome = await flusher.flush();
  assert.equal(uploads.length, 2);
  assert.equal(outcome.status === "ok" && outcome.incomplete, true);
  assert.equal(await queue.size(), 15);
});

test("只上传属于当前用户的点；换人登录遗留的别人的点被丢弃，永不上传", async () => {
  const mine = [sample(), sample()];
  const foreign = [sample({ ownerUserId: "user-b" })];
  const { queue, flusher, uploads } = setup({ upload: acceptAll });
  await queue.enqueue([foreign[0], ...mine]);
  const outcome = await flusher.flush();
  assert.equal(uploads.length, 1);
  assert.deepEqual(
    uploads[0].samples.map((item) => item.clientSampleId),
    mine.map((item) => item.clientSampleId),
  );
  assert.equal(outcome.status === "ok" && outcome.discardedForeign, 1);
  assert.equal(await queue.size(), 0);
});

test("只剩别人的点时 flush 也会把它们清掉（不上传）", async () => {
  const { queue, flusher, uploads } = setup({ upload: acceptAll });
  await queue.enqueue([sample({ ownerUserId: "user-b" })]);
  const outcome = await flusher.flush();
  assert.equal(uploads.length, 0);
  assert.equal(outcome.status === "ok" && outcome.discardedForeign, 1);
  assert.equal(await queue.size(), 0);
});

test("不同门店/设备分组分成不同的请求体", async () => {
  const { queue, flusher, uploads } = setup({ upload: acceptAll });
  await queue.enqueue([
    sample({ storeCode: "1042" }),
    sample({ storeCode: "2000" }),
    sample({ storeCode: "1042" }),
  ]);
  await flusher.flush();
  assert.deepEqual(uploads.map((request) => [request.storeCode, request.samples.length]), [
    ["1042", 2],
    ["2000", 1],
  ]);
});

test("中止信号已触发时不再发请求；信号会传给上传函数", async () => {
  const controller = new AbortController();
  let receivedSignal: AbortSignal | undefined;
  const { queue, flusher, uploads } = setup({
    upload: async (request, uploadOptions) => {
      receivedSignal = uploadOptions.signal;
      return acceptAll(request, uploadOptions);
    },
  });
  await queue.enqueue([sample()]);
  await flusher.flush({ signal: controller.signal });
  assert.equal(receivedSignal, controller.signal);

  await queue.enqueue([sample()]);
  controller.abort();
  const outcome = await flusher.flush({ signal: controller.signal });
  assert.equal(uploads.length, 1);
  assert.equal(outcome.status, "skipped");
  assert.equal(await queue.size(), 1, "被中止时剩余的点必须保留");
});

test("读取当前用户抛错按未登录处理，flush 不会 reject", async () => {
  const queue = createLocationSampleQueue({ storage: createMemoryStorage() });
  await queue.enqueue([sample()]);
  const flusher = createLocationSampleFlusher({
    queue,
    getCurrentUserId: () => {
      throw new Error("secure store down");
    },
    upload: acceptAll,
    isReviewSessionActive: () => false,
    now: () => 1,
  });
  assert.deepEqual(await flusher.flush(), { status: "skipped", reason: "no-user" });
  assert.equal(await queue.size(), 1);
});

test("拒绝类响应会记日志，便于排查为什么点没传上去", async () => {
  const item = sample();
  const { queue, flusher, logs } = setup({
    upload: async () => ({
      accepted: [],
      duplicates: [],
      rejected: [{ clientSampleId: item.clientSampleId, errorCode: "LOCATION_REQUIRED", message: "x" }],
    } satisfies AttendanceLocationSampleBatchResult),
  });
  await queue.enqueue([item]);
  await flusher.flush();
  assert.equal(logs.some((line) => line.includes("拒绝")), true);
});

// ---- 毒批保护：同一分组连续确定业务失败后删除这批点 ----

/** 模拟 unwrapApiEnvelope 收到 success:false 信封时抛出的确定业务失败。 */
function businessError(code: string) {
  return Object.assign(new Error(`业务失败 ${code}`), { apiBusinessError: true, code });
}

const groupA = (overrides: Partial<QueuedLocationSample> = {}) => sample({ storeCode: "1042", ...overrides });
const groupB = (overrides: Partial<QueuedLocationSample> = {}) => sample({ storeCode: "2000", ...overrides });
const keyA = locationSampleGroupKey(groupA());
const keyB = locationSampleGroupKey(groupB());
const iso = (second: number) => `2026-10-13T03:00:${String(second).padStart(2, "0")}.000Z`;

test("isDefiniteBusinessFailure：只认 apiBusinessError === true，其余全是临时失败", () => {
  assert.equal(LOCATION_FLUSH_BUSINESS_FAILURE_DROP_THRESHOLD, 3);
  assert.equal(isDefiniteBusinessFailure(businessError("STORE_REQUIRED")), true);
  assert.equal(isDefiniteBusinessFailure(Object.assign(new Error("x"), { apiBusinessError: false })), false);
  assert.equal(isDefiniteBusinessFailure(Object.assign(new Error("x"), { code: "STORE_REQUIRED" })), false, "只有业务码不够");
  assert.equal(isDefiniteBusinessFailure(new Error("Network Error")), false);
  assert.equal(isDefiniteBusinessFailure(Object.assign(new Error("500"), { response: { status: 500 } })), false);
  assert.equal(
    isDefiniteBusinessFailure(Object.assign(new Error("changed"), { code: "ACCOUNT_SESSION_CHANGED" })),
    false,
  );
  assert.equal(
    isDefiniteBusinessFailure(Object.assign(new LocationSampleBatchResponseError("bad"), { apiBusinessError: true })),
    false,
    "响应格式异常无论如何都是临时失败",
  );
  assert.equal(isDefiniteBusinessFailure(null), false);
  assert.equal(isDefiniteBusinessFailure("STORE_REQUIRED"), false);
});

test("确定业务失败 1~2 次保留（照常退避），第 3 次删除这批并放行下一个分组", async () => {
  const a1 = groupA({ locationCapturedAtUtc: iso(1) });
  const a2 = groupA({ locationCapturedAtUtc: iso(2) });
  const b1 = groupB({ locationCapturedAtUtc: iso(3) });
  const { queue, flusher, uploads, logs, clock } = setup({
    upload: async (request) => {
      if (request.storeCode === "1042") throw businessError("STORE_REQUIRED");
      return acceptAll(request, {});
    },
  });
  await queue.enqueue([a1, a2, b1]);

  // 第 1 次：A 被确定拒绝，整批保留，退避 30s，B 被堵在后面（没有被尝试）
  const first = await flusher.flush({ ignoreBackoff: true });
  assert.equal(first.status, "failed");
  assert.equal(first.status === "failed" && first.droppedAfterBusinessError, undefined, "没删除时不带该字段");
  assert.equal(await queue.size(), 3);
  assert.equal(flusher.getState().nextAttemptAtMs, clock.now + 30_000);
  assert.deepEqual(flusher.getState().businessFailureCounts, { [keyA]: 1 });
  assert.deepEqual(uploads.map((request) => request.storeCode), ["1042"]);

  // 第 2 次：仍然保留，退避 60s
  assert.equal((await flusher.flush({ ignoreBackoff: true })).status, "failed");
  assert.equal(await queue.size(), 3);
  assert.equal(flusher.getState().nextAttemptAtMs, clock.now + 60_000);
  assert.deepEqual(flusher.getState().businessFailureCounts, { [keyA]: 2 });

  // 第 3 次：删除 A 的这一批（2 个点），同一次 flush 里立刻放行 B 并上传成功
  const third = await flusher.flush({ ignoreBackoff: true });
  assert.deepEqual(third, {
    status: "ok",
    batches: 1,
    accepted: 1,
    duplicates: 0,
    rejected: 0,
    discardedForeign: 0,
    incomplete: false,
    droppedAfterBusinessError: 2,
  });
  assert.equal(await queue.size(), 0);
  assert.deepEqual(uploads.map((request) => request.storeCode), ["1042", "1042", "1042", "2000"]);
  assert.deepEqual(flusher.getState().businessFailureCounts, {}, "删除后该分组计数重置");
  assert.equal(flusher.getState().consecutiveFailures, 0, "已放行，退避随之重置");
  assert.equal(
    logs.some((line) => line.includes("连续多次被服务端业务拒绝")),
    true,
  );
});

test("删除时的日志带业务码与点数", async () => {
  const detailLogs: unknown[] = [];
  const queue = createLocationSampleQueue({ storage: createMemoryStorage() });
  const flusher = createLocationSampleFlusher({
    queue,
    getCurrentUserId: () => "user-a",
    upload: async () => {
      throw businessError("TOO_MANY_SAMPLES");
    },
    isReviewSessionActive: () => false,
    now: () => 1,
    log: (message, detail) => {
      if (message.includes("连续多次被服务端业务拒绝")) detailLogs.push(detail);
    },
  });
  await queue.enqueue([groupA(), groupA()]);
  for (let index = 0; index < 3; index += 1) {
    await flusher.flush({ ignoreBackoff: true });
  }
  assert.equal(detailLogs.length, 1);
  assert.deepEqual(detailLogs[0], {
    errorCode: "TOO_MANY_SAMPLES",
    droppedCount: 2,
    consecutiveFailures: 3,
    storeCode: "1042",
  });
});

test("删除后该分组计数重置：之后新入队的同分组点需要重新连续失败 3 次才会删除", async () => {
  const { queue, flusher } = setup({
    upload: async () => {
      throw businessError("STORE_REQUIRED");
    },
  });
  await queue.enqueue([groupA()]);
  for (let index = 0; index < 3; index += 1) {
    await flusher.flush({ ignoreBackoff: true });
  }
  assert.equal(await queue.size(), 0);

  await queue.enqueue([groupA()]);
  await flusher.flush({ ignoreBackoff: true });
  await flusher.flush({ ignoreBackoff: true });
  assert.equal(await queue.size(), 1, "重新计数，前两次仍保留");
  const third = await flusher.flush({ ignoreBackoff: true });
  assert.equal(third.status === "ok" && third.droppedAfterBusinessError, 1);
  assert.equal(await queue.size(), 0);
});

test("临时失败不计数，并把该分组已有的连续计数清零", async () => {
  type FailureMode = "business" | "network" | "server" | "malformed" | "session" | "abort";
  const modes: FailureMode[] = [
    "business", "business", // A=2
    "network",              // 临时失败：清零
    "business", "business", // A=2（不是 4）
    "server",               // 清零
    "business", "business", // A=2
    "malformed",            // 响应格式异常：清零
    "business", "business", // A=2
    "session",              // ACCOUNT_SESSION_CHANGED：清零
    "business", "business", // A=2
    "abort",                // 中止：清零
  ];
  let step = 0;
  const { queue, flusher } = setup({
    upload: async () => {
      const mode = modes[step];
      step += 1;
      if (mode === "business") throw businessError("STORE_REQUIRED");
      if (mode === "network") throw new Error("Network Error");
      if (mode === "server") throw Object.assign(new Error("503"), { response: { status: 503 } });
      if (mode === "malformed") throw new LocationSampleBatchResponseError("bad body");
      if (mode === "session") throw Object.assign(new Error("changed"), { code: "ACCOUNT_SESSION_CHANGED" });
      throw Object.assign(new Error("canceled"), { name: "CanceledError", code: "ERR_CANCELED" });
    },
  });
  await queue.enqueue([groupA(), groupA()]);
  for (let index = 0; index < modes.length; index += 1) {
    await flusher.flush({ ignoreBackoff: true });
    assert.equal(await queue.size(), 2, `第 ${index + 1} 次后点仍应保留（模式 ${modes[index]}）`);
  }
  assert.deepEqual(flusher.getState().businessFailureCounts, {});

  // 之后连续 3 次确定业务失败才会删除
  modes.push("business", "business", "business");
  await flusher.flush({ ignoreBackoff: true });
  await flusher.flush({ ignoreBackoff: true });
  assert.equal(await queue.size(), 2);
  await flusher.flush({ ignoreBackoff: true });
  assert.equal(await queue.size(), 0);
});

test("响应没有确认任何本批样本（无进展）属于临时失败，不计入确定业务失败", async () => {
  let mode: "business" | "empty" = "business";
  const { queue, flusher } = setup({
    upload: async () => {
      if (mode === "business") throw businessError("STORE_REQUIRED");
      return { accepted: [], duplicates: [], rejected: [] };
    },
  });
  await queue.enqueue([groupA()]);
  await flusher.flush({ ignoreBackoff: true }); // A=1
  await flusher.flush({ ignoreBackoff: true }); // A=2
  mode = "empty";
  assert.equal((await flusher.flush({ ignoreBackoff: true })).status, "failed"); // 无进展：清零
  mode = "business";
  await flusher.flush({ ignoreBackoff: true });
  await flusher.flush({ ignoreBackoff: true });
  assert.equal(await queue.size(), 1, "无进展清零后重新计数，不会提前删除");
});

test("成功后清零：先失败 2 次，成功一次，再失败 2 次仍保留", async () => {
  let mode: "business" | "ok" = "business";
  const { queue, flusher } = setup({
    upload: async (request) => {
      if (mode === "business") throw businessError("STORE_REQUIRED");
      return acceptAll(request, {});
    },
  });
  await queue.enqueue([groupA()]);
  await flusher.flush({ ignoreBackoff: true });
  await flusher.flush({ ignoreBackoff: true });
  assert.deepEqual(flusher.getState().businessFailureCounts, { [keyA]: 2 });

  mode = "ok";
  assert.equal((await flusher.flush({ ignoreBackoff: true })).status, "ok");
  assert.equal(await queue.size(), 0);
  assert.deepEqual(flusher.getState().businessFailureCounts, {}, "成功后计数清零");

  mode = "business";
  await queue.enqueue([groupA()]);
  await flusher.flush({ ignoreBackoff: true });
  await flusher.flush({ ignoreBackoff: true });
  assert.equal(await queue.size(), 1, "清零后前两次仍保留");
});

test("两个分组交错时计数互不影响", async () => {
  const a1 = groupA({ locationCapturedAtUtc: iso(1) });
  const b1 = groupB({ locationCapturedAtUtc: iso(2) });
  const a2 = groupA({ locationCapturedAtUtc: iso(3) });
  const { queue, flusher, uploads } = setup({
    upload: async () => {
      throw businessError("STORE_REQUIRED");
    },
  });
  await queue.enqueue([a1, b1]);

  // A 在队首：A=1
  await flusher.flush({ ignoreBackoff: true });
  assert.deepEqual(flusher.getState().businessFailureCounts, { [keyA]: 1 });

  // A 的点被别处清掉（例如换号时被丢弃），B 变成队首；随后又来了更新的 A 点
  await queue.removeByIds([a1.clientSampleId]);
  await queue.enqueue([a2]);

  await flusher.flush({ ignoreBackoff: true }); // B=1，A 仍是 1
  assert.deepEqual(flusher.getState().businessFailureCounts, { [keyA]: 1, [keyB]: 1 });
  await flusher.flush({ ignoreBackoff: true }); // B=2
  assert.deepEqual(flusher.getState().businessFailureCounts, { [keyA]: 1, [keyB]: 2 });

  // B 第 3 次：删除 B 的点并放行 A；A 的这次尝试把 A 记成 2（没有被 B 的删除重置，也没被提前删除）
  const fourth = await flusher.flush({ ignoreBackoff: true });
  assert.equal(fourth.status, "failed");
  assert.equal(fourth.status === "failed" && fourth.droppedAfterBusinessError, 1);
  assert.deepEqual(flusher.getState().businessFailureCounts, { [keyA]: 2 });
  assert.equal(await queue.size(), 1);
  assert.deepEqual(uploads.map((request) => request.storeCode), ["1042", "2000", "2000", "2000", "1042"]);

  // A 第 3 次：删除，队列清空；本次 flush 没发出成功批次，但删除了点，返回 ok 并带 dropped 字段
  const fifth = await flusher.flush({ ignoreBackoff: true });
  assert.deepEqual(fifth, {
    status: "ok",
    batches: 0,
    accepted: 0,
    duplicates: 0,
    rejected: 0,
    discardedForeign: 0,
    incomplete: false,
    droppedAfterBusinessError: 1,
  });
  assert.equal(await queue.size(), 0);
  assert.deepEqual(flusher.getState().businessFailureCounts, {});
});

test("另一分组成功只清它自己的计数，不影响本分组已累计的失败次数", async () => {
  const a1 = groupA({ locationCapturedAtUtc: iso(1) });
  const b1 = groupB({ locationCapturedAtUtc: iso(4) });
  const a2 = groupA({ locationCapturedAtUtc: iso(5) });
  const { queue, flusher } = setup({
    upload: async (request) => {
      if (request.storeCode === "1042") throw businessError("STORE_REQUIRED");
      return acceptAll(request, {});
    },
  });
  await queue.enqueue([a1]);
  await flusher.flush({ ignoreBackoff: true }); // A=1
  await queue.removeByIds([a1.clientSampleId]);
  await queue.enqueue([b1]);
  assert.equal((await flusher.flush({ ignoreBackoff: true })).status, "ok"); // B 成功，只清 B 的计数
  assert.deepEqual(flusher.getState().businessFailureCounts, { [keyA]: 1 });
  await queue.enqueue([a2]);
  await flusher.flush({ ignoreBackoff: true }); // A=2
  await flusher.flush({ ignoreBackoff: true }); // A=3 -> 删除
  assert.equal(await queue.size(), 0);
});

test("审核会话下既不上传也不会因业务失败逻辑删除任何点", async () => {
  const { queue, flusher, uploads } = setup({
    upload: async () => {
      throw businessError("STORE_REQUIRED");
    },
    reviewActive: true,
  });
  await queue.enqueue([groupA()]);
  for (let index = 0; index < 5; index += 1) {
    assert.deepEqual(await flusher.flush({ ignoreBackoff: true }), { status: "skipped", reason: "review-session" });
  }
  assert.equal(uploads.length, 0);
  assert.equal(await queue.size(), 1);
});
