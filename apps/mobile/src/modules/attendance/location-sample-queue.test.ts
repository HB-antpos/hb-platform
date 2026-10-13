import assert from "node:assert/strict";
import { test } from "node:test";
import type { QueuedLocationSample } from "./location-sample-batch";
import {
  LOCATION_SAMPLE_QUEUE_MAX,
  createLocationSampleQueue,
  createSerialRunner,
  normalizeLocationOwnerId,
  type LocationSampleQueueStorage,
} from "./location-sample-queue";

const tick = () => new Promise<void>((resolve) => setImmediate(resolve));

/** 内存存储：每次读写都让出事件循环，模拟 AsyncStorage 的异步性，暴露“读-等-写”交错丢点。 */
function createMemoryStorage(initial: unknown = null) {
  let value: unknown = initial === null ? null : structuredClone(initial);
  const storage: LocationSampleQueueStorage & { peek(): unknown; setRaw(next: unknown): void } = {
    async load() {
      await tick();
      return value === null ? null : structuredClone(value);
    },
    async save(next) {
      await tick();
      value = structuredClone(next);
    },
    async remove() {
      await tick();
      value = null;
    },
    peek: () => value,
    setRaw: (next) => {
      value = next;
    },
  };
  return storage;
}

let counter = 0;
function sample(overrides: Partial<QueuedLocationSample> = {}): QueuedLocationSample {
  counter += 1;
  return {
    clientSampleId: `id-${counter}`,
    ownerUserId: "user-a",
    storeCode: "1042",
    hardwareId: "hw-1",
    eventType: "ShiftSample",
    locationLatitude: -33.8688,
    locationLongitude: 151.2093,
    locationCapturedAtUtc: new Date(Date.UTC(2026, 9, 13, 2, 0, 0) + counter * 1000).toISOString(),
    ...overrides,
  };
}

test("队列容量上限默认 3000", () => {
  assert.equal(LOCATION_SAMPLE_QUEUE_MAX, 3000);
});

test("入队后可取出；超出容量丢弃最旧的点", async () => {
  const queue = createLocationSampleQueue({ storage: createMemoryStorage(), maxSamples: 5 });
  const first = Array.from({ length: 4 }, () => sample());
  assert.deepEqual(await queue.enqueue(first), { size: 4, dropped: 0 });

  const second = Array.from({ length: 3 }, () => sample());
  assert.deepEqual(await queue.enqueue(second), { size: 5, dropped: 2 });

  const { batch } = await queue.takeBatchForOwner("user-a", 200);
  // 最旧的两个（first[0]、first[1]）被丢弃
  assert.deepEqual(
    batch.map((item) => item.clientSampleId),
    [...first.slice(2), ...second].map((item) => item.clientSampleId),
  );
});

test("按生产默认容量 3000：灌入 3005 个点只保留最新的 3000 个", async () => {
  const queue = createLocationSampleQueue({ storage: createMemoryStorage() });
  const all = Array.from({ length: 3005 }, () => sample());
  const result = await queue.enqueue(all);
  assert.deepEqual(result, { size: 3000, dropped: 5 });
  assert.equal(await queue.size(), 3000);
  const { batch } = await queue.takeBatchForOwner("user-a", 1);
  assert.equal(batch[0].clientSampleId, all[5].clientSampleId);
});

test("并发入队 / 取批 / 删除经过互斥串行，不会出现读改写交错丢点", async () => {
  const queue = createLocationSampleQueue({ storage: createMemoryStorage() });
  const seeds = Array.from({ length: 10 }, () => sample());
  await queue.enqueue(seeds);

  const incoming = Array.from({ length: 40 }, () => sample());
  await Promise.all([
    ...incoming.map((item) => queue.enqueue([item])),
    queue.removeByIds(seeds.slice(0, 5).map((item) => item.clientSampleId)),
    queue.takeBatchForOwner("user-a", 200),
    queue.removeByIds(seeds.slice(5, 8).map((item) => item.clientSampleId)),
  ]);

  // 10 个种子删了 8 个，40 个并发入队全部保留
  assert.equal(await queue.size(), 2 + 40);
});

test("createSerialRunner 严格按调用顺序执行，前一个失败不影响后一个", async () => {
  const run = createSerialRunner();
  const order: string[] = [];
  const first = run(async () => {
    await tick();
    order.push("first");
    throw new Error("boom");
  });
  const second = run(async () => {
    order.push("second");
    return 2;
  });
  await assert.rejects(first, /boom/);
  assert.equal(await second, 2);
  assert.deepEqual(order, ["first", "second"]);
});

test("用户绑定：取批只返回当前用户的点，并丢弃（持久化删除）不属于当前用户的点", async () => {
  const storage = createMemoryStorage();
  const queue = createLocationSampleQueue({ storage });
  const mine = [sample(), sample()];
  const foreign = [sample({ ownerUserId: "user-b" }), sample({ ownerUserId: "user-b" })];
  await queue.enqueue([foreign[0], mine[0], foreign[1], mine[1]]);

  const taken = await queue.takeBatchForOwner("USER-A ", 200);
  assert.deepEqual(taken.batch.map((item) => item.clientSampleId), mine.map((item) => item.clientSampleId));
  assert.equal(taken.discardedForeign, 2);
  assert.equal(await queue.size(), 2, "别人的点必须从队列里真正删掉");

  const again = await queue.takeBatchForOwner("user-a", 200);
  assert.equal(again.discardedForeign, 0);
});

test("没有可上传的点属于当前用户时，全部别人的点被清掉，批次为空", async () => {
  const queue = createLocationSampleQueue({ storage: createMemoryStorage() });
  await queue.enqueue([sample({ ownerUserId: "user-b" })]);
  const taken = await queue.takeBatchForOwner("user-a", 200);
  assert.deepEqual(taken, { batch: [], discardedForeign: 1 });
  assert.equal(await queue.size(), 0);
});

test("用户标识为空时不取批也不丢弃任何点", async () => {
  const queue = createLocationSampleQueue({ storage: createMemoryStorage() });
  await queue.enqueue([sample(), sample({ ownerUserId: "user-b" })]);
  assert.deepEqual(await queue.takeBatchForOwner("   ", 200), { batch: [], discardedForeign: 0 });
  assert.equal(await queue.size(), 2);
});

test("取批：按采集时间升序，受 limit 限制，且只取最旧点所在的门店/设备分组", async () => {
  const queue = createLocationSampleQueue({ storage: createMemoryStorage() });
  const t = (second: number) => `2026-10-13T02:00:${String(second).padStart(2, "0")}.000Z`;
  const a3 = sample({ locationCapturedAtUtc: t(3) });
  const a1 = sample({ locationCapturedAtUtc: t(1) });
  const other = sample({ locationCapturedAtUtc: t(2), storeCode: "2000" });
  const a4 = sample({ locationCapturedAtUtc: t(4) });
  await queue.enqueue([a3, a1, other, a4]);

  const limited = await queue.takeBatchForOwner("user-a", 2);
  assert.deepEqual(limited.batch.map((item) => item.clientSampleId), [a1.clientSampleId, a3.clientSampleId]);

  const all = await queue.takeBatchForOwner("user-a", 200);
  // 最旧点 a1 属于 1042 分组；2000 门店的点留到下一批
  assert.deepEqual(all.batch.map((item) => item.clientSampleId), [a1.clientSampleId, a3.clientSampleId, a4.clientSampleId]);
  await queue.removeByIds(all.batch.map((item) => item.clientSampleId));
  const next = await queue.takeBatchForOwner("user-a", 200);
  assert.deepEqual(next.batch.map((item) => item.clientSampleId), [other.clientSampleId]);
});

test("取批不删除；removeByIds 按 id（忽略大小写）删除并返回实际删除数", async () => {
  const queue = createLocationSampleQueue({ storage: createMemoryStorage() });
  const items = [sample({ clientSampleId: "AAA" }), sample({ clientSampleId: "bbb" }), sample()];
  await queue.enqueue(items);
  await queue.takeBatchForOwner("user-a", 200);
  assert.equal(await queue.size(), 3);

  assert.equal(await queue.removeByIds(["aaa", "BBB", "missing"]), 2);
  assert.equal(await queue.size(), 1);
  assert.equal(await queue.removeByIds([]), 0);
  assert.equal(await queue.removeByIds(["aaa"]), 0);
});

test("同一个 clientSampleId 重复入队只保留一份", async () => {
  const queue = createLocationSampleQueue({ storage: createMemoryStorage() });
  const item = sample();
  await queue.enqueue([item]);
  assert.deepEqual(await queue.enqueue([item, item]), { size: 1, dropped: 0 });
});

test("clear 清空队列并移除存储键", async () => {
  const storage = createMemoryStorage();
  const queue = createLocationSampleQueue({ storage });
  await queue.enqueue([sample(), sample()]);
  assert.notEqual(storage.peek(), null);
  await queue.clear();
  assert.equal(storage.peek(), null);
  assert.equal(await queue.size(), 0);
});

test("持久化内容损坏（读取抛错 / 版本不符 / 条目非法）按空队列或过滤处理，下次写入覆盖", async () => {
  const throwing: LocationSampleQueueStorage = {
    async load() {
      throw new SyntaxError("bad json");
    },
    async save() {},
    async remove() {},
  };
  const originalWarn = console.warn;
  console.warn = () => undefined;
  try {
    assert.equal(await createLocationSampleQueue({ storage: throwing }).size(), 0);
  } finally {
    console.warn = originalWarn;
  }

  const wrongVersion = createMemoryStorage({ version: 99, samples: [sample()] });
  assert.equal(await createLocationSampleQueue({ storage: wrongVersion }).size(), 0);

  const good = sample();
  const mixed = createMemoryStorage({ version: 1, samples: [good, { clientSampleId: "x" }, null, 42] });
  const queue = createLocationSampleQueue({ storage: mixed });
  assert.equal(await queue.size(), 1);
  await queue.enqueue([sample()]);
  assert.equal(await queue.size(), 2);
});

test("入队时用户标识统一转小写保存；normalizeLocationOwnerId 行为", () => {
  assert.equal(normalizeLocationOwnerId("  ABC-123 "), "abc-123");
  assert.equal(normalizeLocationOwnerId(""), undefined);
  assert.equal(normalizeLocationOwnerId(undefined), undefined);
  assert.equal(normalizeLocationOwnerId(5), undefined);
});
